//! C# `CliUpdateService`: updates only an already installed, recognised CLI through that CLI's own installer. It never installs
//! anything new, never asks for elevation, never writes a configuration file and never upgrades an unrelated package. Every process
//! goes through the [`CliRunner`], so a check injects a fake and no real claude, codex, agy, npm or winget starts.
//!
//! This is the Windows (C#) updater: native Claude, winget, npm, pi, opencode, Hermes and agy. The Mac's Homebrew updater
//! (`PlatformProfile::cli_updater` = `Homebrew`, M/CLIUpdateService.swift) is not ported here.

use std::collections::BTreeMap;
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::Duration;

use mighty_core::model::AppSnapshot;
use mighty_core::wire;
use regex::Regex;
use tokio::sync::watch;

use super::strings;
use crate::paths;
use crate::runner::{CancellationToken, CliCall, CliRunResult, CliRunner, RunError};

/// One recognised installation of a provider CLI (C# `CliUpdateInstallation`). `method` is native, winget, npm, pi, opencode,
/// hermes, unknown or missing.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CliUpdateInstallation {
    pub provider: String,
    pub version: Option<String>,
    pub method: String,
    pub executable_path: Option<String>,
    pub can_update: bool,
    pub detail: String,
}

impl CliUpdateInstallation {
    pub fn new(provider: &str) -> CliUpdateInstallation {
        CliUpdateInstallation {
            provider: provider.into(),
            version: None,
            method: "unknown".into(),
            executable_path: None,
            can_update: false,
            detail: String::new(),
        }
    }

    fn detail(mut self, detail: String) -> CliUpdateInstallation {
        self.detail = detail;
        self
    }

    fn method(mut self, method: &str) -> CliUpdateInstallation {
        self.method = method.into();
        self
    }

    fn updatable(mut self) -> CliUpdateInstallation {
        self.can_update = true;
        self
    }
}

/// The outcome of one update request (C# `CliUpdateResult`): `status` is updated, current, skipped, failed, cancelled or busy;
/// `output` is bounded diagnostic text, shown only on an explicit details action and never saved.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CliUpdateResult {
    pub provider: String,
    pub status: String,
    pub before_version: Option<String>,
    pub after_version: Option<String>,
    pub method: String,
    pub detail: String,
    pub output: String,
}

impl CliUpdateResult {
    /// `new CliUpdateResult(provider, status)`: no versions, method unknown, no detail.
    pub fn new(provider: &str, status: &str) -> CliUpdateResult {
        CliUpdateResult {
            provider: provider.into(),
            status: status.into(),
            before_version: None,
            after_version: None,
            method: "unknown".into(),
            detail: String::new(),
            output: String::new(),
        }
    }

    pub fn with_detail(mut self, detail: impl Into<String>) -> CliUpdateResult {
        self.detail = detail.into();
        self
    }

    pub fn with_versions(mut self, before: Option<&str>, after: Option<&str>) -> CliUpdateResult {
        self.before_version = before.map(str::to_string);
        self.after_version = after.map(str::to_string);
        self
    }

    pub fn with_method(mut self, method: &str) -> CliUpdateResult {
        self.method = method.into();
        self
    }

    fn of(installation: &CliUpdateInstallation, status: &str, detail: String) -> CliUpdateResult {
        CliUpdateResult {
            before_version: installation.version.clone(),
            method: installation.method.clone(),
            detail,
            ..CliUpdateResult::new(&installation.provider, status)
        }
    }
}

/// The single invocation chosen for an installation: an executable path and an argument list, never a shell command line.
#[derive(Debug, Clone)]
struct Invocation {
    executable: String,
    arguments: Vec<String>,
}

impl Invocation {
    fn new(executable: &str, arguments: &[&str]) -> Option<Invocation> {
        Some(Invocation { executable: executable.into(), arguments: arguments.iter().map(|a| a.to_string()).collect() })
    }
}

/// Why a step stopped: the token (`OperationCanceledException`) or another failure, with its message.
type Step<T> = Result<T, RunError>;

struct Active {
    id: u64,
    token: CancellationToken,
    done: watch::Receiver<bool>,
}

#[derive(Default)]
struct Gate {
    active: Option<Active>,
    next: u64,
    closing: bool,
}

/// C# `CliUpdateService`: one update at a time; a second request while one runs reports busy.
pub struct CliUpdateService {
    runner: Arc<dyn CliRunner>,
    environment: EnvironmentSource,
    home: String,
    metadata_timeout: Duration,
    update_timeout: Duration,
    gate: Mutex<Gate>,
}

/// 1 MiB captured by the runner, the last 8 KiB of it kept for the details action.
pub const DIAGNOSTIC_CHARACTER_CAP: usize = 8192;
const VERSION_TEXT_CAP: usize = 160;
/// `ComponentSection.InstallCommand("gemini")`: Antigravity's own installer, named when an agy elsewhere is left alone.
const ANTIGRAVITY_INSTALL_COMMAND: &str = "irm https://antigravity.google/cli/install.ps1 | iex";

/// Where the service reads the CLI environment from, each time it plans an update (the app's may change: a refreshed PATH).
pub type EnvironmentSource = Arc<dyn Fn() -> BTreeMap<String, String> + Send + Sync>;

impl CliUpdateService {
    /// `environment` is the CLI environment the PATH and the installers' folders are read from (C#: the process's variables), `home`
    /// the user's profile folder. Nothing falls back to the real profile: a folder the environment does not name is not looked in.
    pub fn new(runner: Arc<dyn CliRunner>, environment: BTreeMap<String, String>, home: impl Into<String>) -> CliUpdateService {
        Self::with_environment_source(runner, Arc::new(move || environment.clone()), home)
    }

    /// [`CliUpdateService::new`] with the environment read each time an update is planned, on a blocking thread (the app's comes from
    /// its shell environment, which may read the registry): making the service reads nothing.
    pub fn with_environment_source(
        runner: Arc<dyn CliRunner>,
        environment: EnvironmentSource,
        home: impl Into<String>,
    ) -> CliUpdateService {
        CliUpdateService {
            runner,
            environment,
            home: home.into(),
            metadata_timeout: Duration::from_secs(10),
            update_timeout: Duration::from_secs(5 * 60),
            gate: Mutex::new(Gate::default()),
        }
    }

    pub fn with_timeouts(mut self, metadata: Duration, update: Duration) -> CliUpdateService {
        self.metadata_timeout = metadata;
        self.update_timeout = update;
        self
    }

    fn lock(&self) -> MutexGuard<'_, Gate> {
        self.gate.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// The app checks at start only when the saved switch is a JSON true.
    pub fn should_run_at_startup(snapshot: &AppSnapshot) -> bool {
        snapshot.auto_update_clis == Some(true)
    }

    /// macOS `ProviderOptions.label`: the name a result row shows.
    pub fn provider_label(provider: &str) -> &'static str {
        match provider {
            "codex" => "Codex",
            "gemini" => "Antigravity",
            "pi" => "pi",
            "opencode" => "opencode",
            "hermes" => "Hermes",
            _ => "Claude",
        }
    }

    /// The row the section shows ("{provider} · {status}"), so no result copy is assembled outside Core.
    pub fn result_row(result: &CliUpdateResult) -> String {
        strings::result_row_template()
            .replace("{provider}", Self::provider_label(&result.provider))
            .replace("{status}", &strings::status_label(&result.status))
    }

    /// The version line ("{before} → {after}"), or None when nothing changed.
    pub fn version_change(result: &CliUpdateResult) -> Option<String> {
        match (result.before_version.as_deref(), result.after_version.as_deref()) {
            (Some(before), Some(after)) if !before.is_empty() && !after.is_empty() && before != after => {
                Some(strings::version_change_template().replace("{before}", before).replace("{after}", after))
            }
            _ => None,
        }
    }

    /// What is installed for `provider` and how it would be updated; starts no installer.
    pub async fn inspect(&self, provider: &str, cancel: &CancellationToken) -> CliUpdateInstallation {
        if self.lock().closing {
            return CliUpdateInstallation::new(provider).detail(strings::detail_closing());
        }
        let machine = self.machine().await;
        match self.plan(&machine, provider, cancel).await {
            Ok((installation, _)) => installation,
            Err(RunError::Cancelled) => CliUpdateInstallation::new(provider).detail(strings::detail_inspect_cancelled()),
            Err(RunError::Failed(_)) => CliUpdateInstallation::new(provider).detail(strings::detail_inspect_failed()),
        }
    }

    /// One update at a time: a second request while one runs reports busy, a closing app cancelled.
    pub async fn update(&self, provider: &str, cancel: &CancellationToken) -> CliUpdateResult {
        let (id, token, done) = {
            let mut gate = self.lock();
            if gate.closing {
                return CliUpdateResult::new(provider, "cancelled").with_detail(strings::detail_closing());
            }
            if gate.active.is_some() {
                return CliUpdateResult::new(provider, "busy").with_detail(strings::detail_busy());
            }
            if cancel.is_cancelled() {
                return CliUpdateResult::new(provider, "cancelled").with_detail(strings::detail_cancelled());
            }
            gate.next += 1;
            let token = cancel.child_token();
            let (done, receiver) = watch::channel(false);
            gate.active = Some(Active { id: gate.next, token: token.clone(), done: receiver });
            (gate.next, token, done)
        };
        let _finished = Finished { service: self, id, done };
        self.perform(provider, &token).await
    }

    /// The start-up and 업데이트 하기 pass: one provider after another, never two installers at once.
    pub async fn update_all(&self, cancel: &CancellationToken) -> Vec<CliUpdateResult> {
        let mut results = Vec::new();
        for provider in wire::PROVIDERS {
            results.push(self.update(provider, cancel).await);
        }
        results
    }

    /// Stops the running installer together with its whole process group and waits for its result, so the section never shows a
    /// half-finished run.
    pub async fn cancel(&self) {
        let done = {
            let gate = self.lock();
            gate.active.as_ref().map(|active| {
                active.token.cancel();
                active.done.clone()
            })
        };
        if let Some(mut done) = done {
            let _ = done.wait_for(|finished| *finished).await;
        }
    }

    /// Closing the app: refuse new work, then cancel cleanly.
    pub async fn shutdown(&self) {
        self.lock().closing = true;
        self.cancel().await;
    }

    async fn perform(&self, provider: &str, token: &CancellationToken) -> CliUpdateResult {
        let mut installation = CliUpdateInstallation::new(provider);
        let outcome: Step<CliUpdateResult> = async {
            cancelled(token)?;
            let machine = self.machine().await;
            let (planned, invocation) = self.plan(&machine, provider, token).await?;
            installation = planned;
            cancelled(token)?;
            let Some(invocation) = invocation else {
                return Ok(CliUpdateResult::of(&installation, "skipped", installation.detail.clone()));
            };
            let call = CliCall::new(invocation.executable, invocation.arguments, self.update_timeout);
            let run = self.runner.run(call, token.clone()).await?;
            cancelled(token)?;
            let output = bounded(&run);
            if run.exit_code != 0 {
                return Ok(CliUpdateResult {
                    output,
                    ..CliUpdateResult::of(&installation, "failed", strings::detail_failed_exit(run.exit_code))
                });
            }
            let after = self.installed_command(&machine, provider, token).await?;
            cancelled(token)?;
            let Some((_, version)) = after.filter(|(_, version)| !version.is_empty()) else {
                return Ok(CliUpdateResult {
                    output,
                    ..CliUpdateResult::of(&installation, "failed", strings::detail_version_recheck_failed())
                });
            };
            let changed = installation.version.as_deref() != Some(version.as_str());
            let detail = if changed { strings::detail_updated() } else { strings::detail_unchanged() };
            Ok(CliUpdateResult {
                after_version: Some(version),
                output,
                ..CliUpdateResult::of(&installation, if changed { "updated" } else { "current" }, detail)
            })
        }
        .await;
        match outcome {
            Ok(result) => result,
            Err(RunError::Cancelled) => CliUpdateResult::of(&installation, "cancelled", strings::detail_cancelled()),
            Err(RunError::Failed(message)) => CliUpdateResult::of(&installation, "failed", paths::utf16_prefix(&message, 600)),
        }
    }

    /// What is on this computer for a plan to look at: the environment as read now, on a blocking thread, and the home folder.
    async fn machine(&self) -> Arc<Machine> {
        let (environment, home) = (self.environment.clone(), self.home.clone());
        Arc::new(blocking(move || Machine { environment: environment(), home }).await)
    }

    async fn plan(
        &self,
        machine: &Arc<Machine>,
        provider: &str,
        token: &CancellationToken,
    ) -> Step<(CliUpdateInstallation, Option<Invocation>)> {
        if !wire::PROVIDERS.contains(&provider) {
            return Ok((CliUpdateInstallation::new(provider).detail(strings::detail_unsupported_provider()), None));
        }
        let Some((path, version)) = self.installed_command(machine, provider, token).await? else {
            let (on, name) = (machine.clone(), mighty_run::providers::executable(provider).to_string());
            let present = blocking(move || on.candidates(&name).iter().any(|c| is_file(c))).await;
            let installation = CliUpdateInstallation::new(provider);
            return Ok((
                if present {
                    installation.detail(strings::detail_version_unknown())
                } else {
                    installation.method("missing").detail(strings::detail_missing())
                },
                None,
            ));
        };
        // Links resolved, package.json read, folders looked at: on a blocking thread.
        let (on, provider) = (machine.clone(), provider.to_string());
        Ok(blocking(move || on.analyze(&provider, path, version)).await)
    }

    /// The first candidate that answers `--version` (exit 0, in time, a version), as `(path, version)`; the candidates that exist
    /// found on a blocking thread.
    async fn installed_command(&self, machine: &Arc<Machine>, provider: &str, token: &CancellationToken) -> Step<Option<(String, String)>> {
        if !wire::PROVIDERS.contains(&provider) {
            return Ok(None);
        }
        let executable = mighty_run::providers::executable(provider);
        let (on, name) = (machine.clone(), executable.to_string());
        let existing: Vec<String> = blocking(move || on.candidates(&name).into_iter().filter(|c| is_file(c)).collect()).await;
        for path in existing {
            cancelled(token)?;
            let run = match self.runner.run(CliCall::new(&path, ["--version"], self.metadata_timeout), token.clone()).await {
                Ok(run) => run,
                Err(RunError::Cancelled) => return Err(RunError::Cancelled),
                Err(RunError::Failed(_)) => continue,
            };
            if run.exit_code != 0 || run.timed_out {
                continue;
            }
            // Only Hermes's first line is its version, as the runtime reads it (CliDiscovery.VersionText).
            let text = mighty_run::providers::version_text(executable, &run.output);
            if text.is_empty() {
                continue;
            }
            return Ok(Some((path, paths::utf16_prefix(&text, VERSION_TEXT_CAP))));
        }
        Ok(None)
    }
}

/// The environment and the home folder one plan reads, and the file questions it asks (on a blocking thread).
struct Machine {
    environment: BTreeMap<String, String>,
    home: String,
}

impl Machine {
    /// How the installation at `path` (answering `version`) is updated, if it is: the sync half of C#'s `PlanAsync`.
    fn analyze(&self, provider: &str, path: String, version: String) -> (CliUpdateInstallation, Option<Invocation>) {
        let resolved = paths::resolve(&path);
        let installation = CliUpdateInstallation {
            version: Some(version.clone()),
            executable_path: Some(path.clone()),
            detail: strings::detail_unknown_method(),
            ..CliUpdateInstallation::new(provider)
        };
        let get = mighty_run::strings::get;
        match provider {
            // pi updates itself, whichever way it was installed (`pi update self`; M/CLIUpdateService.swift).
            "pi" => {
                return (
                    installation.method("pi").updatable().detail(get("settings.cliUpdate.detailPi")),
                    Invocation::new(&path, &["update", "self"]),
                );
            }
            "opencode" => {
                // OpenCode 1 (npm opencode-ai) is replaced from the setup screen: its own upgrade stays on 1.
                if !mighty_run::acp::accounts::opencode_supported(&version) {
                    return (installation.method("opencode").detail(get("settings.cliUpdate.detailOpencodeV1")), None);
                }
                // OpenCode 2 upgrades itself, finds out how it was installed (npm, its script) and asks nothing.
                return (
                    installation.method("opencode").updatable().detail(get("settings.cliUpdate.detailOpencode")),
                    Invocation::new(&path, &["upgrade"]),
                );
            }
            // Hermes updates its own checkout and environment; --yes keeps it from waiting on a prompt.
            "hermes" => {
                return (
                    installation.method("hermes").updatable().detail(get("settings.cliUpdate.detailHermes")),
                    Invocation::new(&path, &["update", "--yes"]),
                );
            }
            _ => {}
        }
        if let Some(package) = winget_package(&resolved) {
            let installation = installation.method("winget");
            let Some(winget) = self.candidates("winget").into_iter().find(|c| is_file(c)) else {
                return (installation.detail(strings::detail_winget_runtime_missing()), None);
            };
            // Exactly the one package this installation belongs to: --id with --exact, never --all, no interactive or elevation prompt.
            return (
                installation.updatable().detail(strings::detail_winget_plan()),
                Invocation::new(
                    &winget,
                    &[
                        "upgrade",
                        "--id",
                        &package,
                        "--exact",
                        "--silent",
                        "--accept-source-agreements",
                        "--accept-package-agreements",
                        "--disable-interactivity",
                    ],
                ),
            );
        }
        if provider == "gemini" {
            // Antigravity's installer puts one agy.exe in %LOCALAPPDATA%\agy\bin, which `agy update` replaces; a copy anywhere else is
            // left alone (M/CLIUpdateService.swift:146-157).
            let installed = paths::join(&self.antigravity_install_folder(), &["agy.exe"]);
            if !paths::same(Some(&resolved), Some(&installed)) {
                let detail = mighty_run::strings::get_with(
                    "settings.cliUpdate.detailAntigravityElsewhereWindows",
                    &[("command", ANTIGRAVITY_INSTALL_COMMAND)],
                );
                return (installation.detail(detail), None);
            }
            return (
                installation.method("native").updatable().detail(get("settings.cliUpdate.detailNativeAntigravity")),
                Invocation::new(&path, &["update"]),
            );
        }
        if provider == "claude" && self.native_claude(&resolved) {
            return (
                installation.method("native").updatable().detail(strings::detail_native_claude()),
                Invocation::new(&path, &["update"]),
            );
        }
        if let Some(npm) = npm_package(&resolved, provider) {
            let installation = installation.method("npm");
            if !release(&npm.version) {
                return (installation.detail(strings::detail_npm_prerelease()), None);
            }
            let Some((node, script)) = self.find_npm_runtime(&npm.prefix) else {
                return (installation.detail(strings::detail_npm_runtime_missing()), None);
            };
            // The official package only, in the prefix it is already installed in.
            let latest = format!("{}@latest", npm.name);
            return (
                installation.updatable().detail(strings::detail_npm_plan()),
                Invocation::new(&node, &[&script, "install", "--global", "--prefix", &npm.prefix, &latest, "--no-audit", "--no-fund"]),
            );
        }
        (installation, None)
    }

    /// The PATH candidates, then Antigravity's installer folder (it reaches PATH for new sessions only), then npm's global folder
    /// (opencode.cmd) and Hermes's own bin folder.
    fn candidates(&self, name: &str) -> Vec<String> {
        let mut all = paths::path_candidates(&self.environment, name);
        if name == "agy"
            && let Some(local) = paths::variable(&self.environment, "LOCALAPPDATA")
            && paths::rooted(local)
            && !local.contains('\0')
        {
            all.push(paths::join(&self.antigravity_install_folder(), &["agy.exe"]));
        }
        all.extend(installer_copies(name, &self.environment));
        all
    }

    /// `ProviderCatalog.AntigravityInstallFolder`: `%LOCALAPPDATA%\agy\bin`.
    fn antigravity_install_folder(&self) -> String {
        let local = paths::variable(&self.environment, "LOCALAPPDATA").unwrap_or("");
        paths::join(local, &["agy", "bin"])
    }

    /// The native installer keeps its versions under `<home>\.local\share\claude\versions` (`XDG_DATA_HOME` when set), either as the
    /// executable itself or one folder per version (the Windows layout).
    fn native_claude(&self, executable: &str) -> bool {
        let mut roots = vec![paths::join(&self.home, &[".local", "share", "claude", "versions"])];
        if let Some(data) = paths::variable(&self.environment, "XDG_DATA_HOME")
            && !data.is_empty()
            && paths::rooted(data)
            && !data.contains('\0')
        {
            roots.push(paths::join(data, &["claude", "versions"]));
        }
        let parent = paths::parent(executable);
        let grandparent = parent.as_deref().and_then(paths::parent);
        roots.iter().any(|root| paths::same(parent.as_deref(), Some(root)) || paths::same(grandparent.as_deref(), Some(root)))
    }

    /// npm itself is started through Node with npm's own cli script, never through a `.cmd` shim.
    fn find_npm_runtime(&self, prefix: &str) -> Option<(String, String)> {
        let scripts = [
            paths::join(prefix, &["node_modules", "npm", "bin", "npm-cli.js"]),
            paths::join(prefix, &["lib", "node_modules", "npm", "bin", "npm-cli.js"]),
        ];
        let script = scripts.into_iter().find(|s| is_file(s))?;
        let node = self.candidates("node").into_iter().find(|c| is_file(c))?;
        Some((node, script))
    }
}

/// Clears the active run when [`CliUpdateService::update`] ends or is dropped, and wakes a waiting cancel.
struct Finished<'a> {
    service: &'a CliUpdateService,
    id: u64,
    done: watch::Sender<bool>,
}

impl Drop for Finished<'_> {
    fn drop(&mut self) {
        let mut gate = self.service.lock();
        if gate.active.as_ref().is_some_and(|active| active.id == self.id) {
            gate.active = None;
        }
        drop(gate);
        self.done.send_replace(true);
    }
}

/// `work` on a blocking thread (file system questions, the environment's read), a panic in it carried over.
async fn blocking<T: Send + 'static>(work: impl FnOnce() -> T + Send + 'static) -> T {
    match tokio::task::spawn_blocking(work).await {
        Ok(value) => value,
        Err(error) => std::panic::resume_unwind(error.into_panic()),
    }
}

fn cancelled(token: &CancellationToken) -> Step<()> {
    if token.is_cancelled() { Err(RunError::Cancelled) } else { Ok(()) }
}

fn is_file(path: &str) -> bool {
    std::path::Path::new(path).is_file()
}

/// `CliAccountSupport.InstallerCopies`: where an installer leaves a CLI that reaches PATH for new sessions only — opencode's npm
/// shim in `%APPDATA%\npm`, Hermes's bin folder (`HERMES_HOME`, else `%LOCALAPPDATA%\hermes`). Only folders the environment names.
pub fn installer_copies(name: &str, environment: &BTreeMap<String, String>) -> Vec<String> {
    let rooted = |name: &str| paths::variable(environment, name).filter(|p| !p.is_empty() && !p.contains('\0') && paths::rooted(p));
    match name {
        "opencode" => rooted("APPDATA").map(|roaming| vec![paths::join(roaming, &["npm", "opencode.cmd"])]).unwrap_or_default(),
        "hermes" => {
            let home =
                rooted("HERMES_HOME").map(str::to_string).or_else(|| rooted("LOCALAPPDATA").map(|local| paths::join(local, &["hermes"])));
            home.map(|home| vec![paths::join(&home, &["bin", "hermes.exe"]), paths::join(&home, &["bin", "hermes.cmd"])])
                .unwrap_or_default()
        }
        _ => Vec::new(),
    }
}

/// winget installs a package under `...\WinGet\Packages\<PackageIdentifier>_<hash>\` and links it from `...\WinGet\Links`: the
/// identifier read out of the install location names exactly the package that is installed, never a guessed one.
fn winget_package(executable: &str) -> Option<String> {
    let parts: Vec<&str> = executable.split(['\\', '/']).filter(|part| !part.is_empty()).collect();
    for i in 1..parts.len().saturating_sub(1) {
        if !parts[i].eq_ignore_ascii_case("Packages") || !parts[i - 1].eq_ignore_ascii_case("WinGet") {
            continue;
        }
        let folder = parts[i + 1];
        let id = match folder.rfind('_') {
            Some(cut) if cut > 0 => &folder[..cut],
            _ => folder,
        };
        if winget_identifier(id) {
            return Some(id.to_string());
        }
    }
    None
}

/// `\A[A-Za-z0-9][A-Za-z0-9.+-]{0,127}\z`.
fn winget_identifier(id: &str) -> bool {
    let bytes = id.as_bytes();
    !bytes.is_empty()
        && bytes.len() <= 128
        && bytes[0].is_ascii_alphanumeric()
        && bytes[1..].iter().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'+' | b'-'))
}

/// `\A[0-9]+\.[0-9]+\.[0-9]+(?:\+[0-9A-Za-z.-]+)?\z`: a release, never a prerelease channel.
fn release(version: &str) -> bool {
    static PATTERN: std::sync::OnceLock<Regex> = std::sync::OnceLock::new();
    PATTERN.get_or_init(|| Regex::new(r"\A[0-9]+\.[0-9]+\.[0-9]+(?:\+[0-9A-Za-z.-]+)?\z").expect("pattern")).is_match(version)
}

struct NpmInstallation {
    prefix: String,
    name: String,
    version: String,
}

/// npm `--global` on Windows puts its shims straight in the prefix folder and the package under `<prefix>\node_modules\<name>`, so
/// the executable's own folder is the prefix (a Unix-shaped prefix keeps the shim in `<prefix>/bin`). The package.json must name the
/// official package and declare a bin entry for this CLI, or the install is not recognised.
fn npm_package(executable: &str, provider: &str) -> Option<NpmInstallation> {
    let name = match provider {
        "claude" => "@anthropic-ai/claude-code",
        "codex" => "@openai/codex",
        _ => return None,
    };
    let prefix = paths::parent(executable)?;
    let mut roots = vec![paths::join(&prefix, &["node_modules"])];
    if let Some(parent) = paths::parent(&prefix) {
        roots.push(paths::join(&parent, &["lib", "node_modules"]));
    }
    let lib = paths::join("lib", &["node_modules"]).to_lowercase();
    for root in roots {
        let package = paths::join(&root, &name.split('/').collect::<Vec<_>>());
        let Some(json) = metadata(&package) else { continue };
        if json.name != name || !json.bin.iter().any(|b| b == provider) {
            continue;
        }
        let target = paths::parent(&root).and_then(|p| paths::parent(&p));
        let prefix = match target {
            Some(target) if root.to_lowercase().ends_with(&lib) => target,
            _ => prefix.clone(),
        };
        return Some(NpmInstallation { prefix, name: name.into(), version: json.version });
    }
    None
}

struct PackageJson {
    name: String,
    version: String,
    bin: Vec<String>,
}

/// A package.json of at most 256 KiB, read as JSON: its name, version and bin names.
fn metadata(package_root: &str) -> Option<PackageJson> {
    let file = paths::join(package_root, &["package.json"]);
    let info = std::fs::metadata(&file).ok()?;
    if !info.is_file() || info.len() > 262_144 {
        return None;
    }
    let root: serde_json::Value = serde_json::from_slice(&std::fs::read(&file).ok()?).ok()?;
    let object = root.as_object()?;
    let text = |key: &str| object.get(key).and_then(serde_json::Value::as_str).unwrap_or("").to_string();
    let (name, version) = (text("name"), text("version"));
    let bin = match object.get("bin") {
        Some(serde_json::Value::Object(entries)) => entries.keys().cloned().collect(),
        Some(serde_json::Value::String(_)) if !name.is_empty() => vec![name[name.rfind('/').map_or(0, |at| at + 1)..].to_string()],
        _ => Vec::new(),
    };
    Some(PackageJson { name, version, bin })
}

/// stdout then stderr, control characters dropped (but LF and tab), the last 8 KiB kept: shown only on request, never saved.
fn bounded(run: &CliRunResult) -> String {
    let text = if run.error_output.is_empty() { run.output.clone() } else { format!("{}\n{}", run.output, run.error_output) };
    let safe: String = text.chars().filter(|c| !c.is_control() || matches!(c, '\n' | '\t')).collect();
    paths::utf16_suffix(&safe, DIAGNOSTIC_CHARACTER_CAP)
}
