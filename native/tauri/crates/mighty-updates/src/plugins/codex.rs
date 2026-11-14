//! C# `CodexPluginReader` (and its half of PluginAutoUpdates.cs): the workspace's Codex plugins through the installed Codex CLI's
//! own plugin subcommands and its user-level registry —
//!
//! ```text
//! codex --version
//! codex plugin list --help / plugin add --help
//! codex plugin marketplace list --help / plugin marketplace upgrade --help
//! codex plugin list --json --available
//! codex plugin marketplace list --json
//! ```
//!
//! The version never gates anything: this CLI feature still moves, so — as CodexPluginService.command does — each subcommand's help
//! is read first and the flags this build offers are confirmed before any is used; a build missing one is "unsupported". Install
//! (`plugin add <id> --json`, user level only) and marketplace upgrade (`plugin marketplace upgrade <name> --json`, a registered Git
//! source only) are the two mutations.

use std::collections::BTreeMap;
use std::sync::Arc;

use mighty_core::model::Workspace;
use mighty_core::wire;
use mighty_platform::BoxFuture;
use serde_json::Value;

use super::claude::{Core, Operating, Probe, Step, Stop, cancelled, default_probes};
use super::model::{ClaudePluginOperationResult, ClaudePluginSnapshot, PluginReader, status};
use super::operations::PluginAutoUpdateResult;
use super::strings::{auto_update as auto_strings, codex as strings, plugin as shared};
use super::support::{self, OUTPUT_CAP};
use crate::paths;
use crate::runner::{CancellationToken, CliCall, CliRunner, RunError};

/// macOS CodexPluginService: reads 20 s (the Claude reader's [`super::claude::READ_TIMEOUT`]), probes min(4, read), an operation 180 s.
pub const PROBE_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(4);
pub const OPERATION_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(180);

/// The four help probes and the flags each answer must name, in the order CodexPluginService.command runs them.
pub const CAPABILITIES: [(&[&str], &[&str]); 4] = [
    (&["plugin", "list", "--help"], &["--json", "--available"]),
    (&["plugin", "add", "--help"], &["--json"]),
    (&["plugin", "marketplace", "list", "--help"], &["--json"]),
    (&["plugin", "marketplace", "upgrade", "--help"], &["--json"]),
];

/// C# `CodexPluginReader`.
#[derive(Clone)]
pub struct CodexPluginReader {
    core: Arc<Core>,
}

impl CodexPluginReader {
    /// `environment` as [`super::ClaudePluginReader::new`] takes it; only `GIT_TERMINAL_PROMPT=0` is laid over it.
    pub fn new(runner: Arc<dyn CliRunner>, environment: BTreeMap<String, String>) -> CodexPluginReader {
        let (is_executable, directory_exists) = default_probes();
        Self::with_probes(runner, environment, is_executable, directory_exists)
    }

    pub fn with_probes(
        runner: Arc<dyn CliRunner>,
        environment: BTreeMap<String, String>,
        is_executable: Probe,
        directory_exists: Probe,
    ) -> CodexPluginReader {
        CodexPluginReader { core: Arc::new(Core::new(runner, Self::environment(environment), Vec::new(), is_executable, directory_exists)) }
    }

    /// The one switch macOS sets: git never stops and asks for a password while a read runs. The user's own `CODEX_HOME` still
    /// decides which user-level registry is read.
    pub fn environment(mut supplied: BTreeMap<String, String>) -> BTreeMap<String, String> {
        paths::set_variable(&mut supplied, "GIT_TERMINAL_PROMPT", "0");
        supplied
    }

    /// The first codex on PATH that answers `--version`, then the capability probe. The version is shown in the header only.
    async fn command(core: &Core, cwd: &str, cancel: &CancellationToken) -> Step<(String, String)> {
        let (executable, version) =
            core.command("codex", cwd, cancel, |_| Ok(()), (strings::detail_unknown_version(), strings::detail_missing_cli())).await?;
        Self::probe(core, &executable, cwd, cancel).await?;
        Ok((executable, version))
    }

    /// Asks the CLI itself which plugin subcommands and flags it has; reading `--help` changes nothing. A probe that fails, times out
    /// or does not name every flag this screen would use is "unsupported".
    async fn probe(core: &Core, executable: &str, cwd: &str, cancel: &CancellationToken) -> Step<()> {
        for (arguments, flags) in CAPABILITIES {
            let probe = match core.runner.run(core.call(executable, arguments, cwd, PROBE_TIMEOUT), cancel.clone()).await {
                Ok(probe) => probe,
                Err(RunError::Cancelled) => return Err(Stop::Cancelled),
                Err(RunError::Failed(_)) => return Err(Stop::failure(status::UNSUPPORTED, strings::detail_unsupported())),
            };
            if probe.timed_out || probe.exit_code != 0 || !flags.iter().all(|flag| probe.output.contains(flag)) {
                return Err(Stop::Failure {
                    status: status::UNSUPPORTED,
                    detail: strings::detail_unsupported(),
                    output: support::output(&probe),
                });
            }
        }
        Ok(())
    }

    /// The two read subcommands and the parse; a CLI that answered but warned on stderr keeps its warning and says the list may be
    /// stale, as macOS readSnapshot does.
    async fn list(core: &Core, executable: &str, cwd: &str, version: &str, cancel: &CancellationToken) -> Step<ClaudePluginSnapshot> {
        let listing =
            core.read(executable, &["plugin", "list", "--json", "--available"], cwd, strings::detail_listing_failed(), cancel).await?;
        let markets =
            core.read(executable, &["plugin", "marketplace", "list", "--json"], cwd, shared::detail_marketplaces_failed(), cancel).await?;
        let snapshot = support::parse_codex_snapshot(&listing.output, &markets.output, cwd, Some(version));
        let warnings = support::display(Some(format!("{}\n{}", listing.error_output, markets.error_output).trim()), OUTPUT_CAP);
        if warnings.is_empty() || snapshot.status != status::READY {
            return Ok(snapshot);
        }
        let detail = snapshot.detail.clone() + &strings::detail_warning_suffix();
        Ok(ClaudePluginSnapshot { diagnostic_output: warnings, detail, ..snapshot })
    }

    async fn read(core: Arc<Core>, workspace_path: String, cancel: CancellationToken) -> ClaudePluginSnapshot {
        let read = async {
            let cwd = core.local_directory(&workspace_path)?;
            let (executable, version) = Self::command(&core, &cwd, &cancel).await?;
            Self::list(&core, &executable, &cwd, &version, &cancel).await
        };
        read.await.unwrap_or_else(Stop::snapshot)
    }

    async fn run_operation(
        core: Arc<Core>,
        _operating: Operating,
        plugin_id: Option<String>,
        marketplace: Option<String>,
        workspace_path: String,
        cancel: CancellationToken,
    ) -> ClaudePluginOperationResult {
        let work = async {
            let cwd = core.local_directory(&workspace_path)?;
            let (executable, version) = Self::command(&core, &cwd, &cancel).await?;
            let snapshot = Self::list(&core, &executable, &cwd, &version, &cancel).await?;
            if snapshot.status != status::READY {
                return Ok(ClaudePluginOperationResult::new(status::FAILED, snapshot.detail).with_output(snapshot.diagnostic_output));
            }
            cancelled(&cancel)?;
            let arguments: Vec<String> = match (&plugin_id, &marketplace) {
                (Some(plugin_id), _) => {
                    if snapshot.installed.iter().any(|p| p.plugin_id == *plugin_id) {
                        return Ok(ClaudePluginOperationResult::new(status::SKIPPED, strings::install_skipped()));
                    }
                    if !snapshot.available.iter().any(|p| p.id == *plugin_id) {
                        return Ok(ClaudePluginOperationResult::new(status::FAILED, strings::install_not_found()));
                    }
                    ["plugin", "add", plugin_id.as_str(), "--json"].map(str::to_string).to_vec()
                }
                (None, marketplace) => {
                    let marketplace = marketplace.as_deref().unwrap_or("");
                    let Some(selected) = snapshot.marketplaces.iter().find(|m| m.name == marketplace) else {
                        return Ok(ClaudePluginOperationResult::new(status::FAILED, shared::marketplace_not_registered()));
                    };
                    if selected.source_kind != "git" {
                        return Ok(ClaudePluginOperationResult::new(status::SKIPPED, strings::marketplace_not_git()));
                    }
                    ["plugin", "marketplace", "upgrade", marketplace, "--json"].map(str::to_string).to_vec()
                }
            };
            let call = CliCall::new(&executable, arguments, OPERATION_TIMEOUT)
                .environment(Some(core.environment.clone()))
                .working_directory(Some(cwd.clone()));
            let result = match core.operate(call, &cancel).await {
                Ok(result) => result,
                Err(refused) => return Ok(refused),
            };
            cancelled(&cancel)?;
            let output = support::output(&result);
            if result.timed_out {
                return Ok(ClaudePluginOperationResult::new(status::FAILED, shared::detail_incomplete()).with_output(output));
            }
            let failed = || ClaudePluginOperationResult::new(status::FAILED, strings::operation_failed()).with_output(output.clone());
            let Ok(root) = serde_json::from_str::<Value>(&result.output) else { return Ok(failed()) };
            let Some(root) = root.as_object().filter(|_| result.exit_code == 0) else { return Ok(failed()) };
            let text = |name: &str| root.get(name).and_then(Value::as_str);
            if let Some(plugin_id) = &plugin_id {
                let Some((name, market)) = support::plugin_parts(Some(plugin_id)) else { return Ok(failed()) };
                // OS-bound substitution (docs/windows-plugins.md): macOS asks installedPath to start with "/", this asks for a
                // rooted path — the same "an absolute path" question.
                let installed_path = text("installedPath");
                if text("pluginId") != Some(plugin_id.as_str())
                    || text("name") != Some(name.as_str())
                    || text("marketplaceName") != Some(market.as_str())
                    || !installed_path.is_some_and(|path| !path.contains('\0') && paths::rooted(path))
                {
                    return Ok(ClaudePluginOperationResult::new(status::FAILED, shared::install_unconfirmed()).with_output(output));
                }
                let verified = Self::list(&core, &executable, &cwd, &version, &cancel).await?;
                if !verified.installed.iter().any(|p| p.plugin_id == *plugin_id) {
                    return Ok(ClaudePluginOperationResult::new(status::FAILED, strings::install_verify_failed()).with_output(output));
                }
                return Ok(ClaudePluginOperationResult::new(status::SUCCEEDED, strings::install_succeeded()).with_output(output));
            }
            let selected: Vec<&str> = match root.get("selectedMarketplaces") {
                Some(Value::Array(values)) => values.iter().filter_map(Value::as_str).collect(),
                _ => Vec::new(),
            };
            let roots = root.get("upgradedRoots").is_some_and(Value::is_array);
            let clean = root.get("errors").and_then(Value::as_array).is_some_and(Vec::is_empty);
            if selected != [marketplace.as_deref().unwrap_or("")] || !roots || !clean {
                return Ok(ClaudePluginOperationResult::new(status::FAILED, strings::marketplace_refresh_unconfirmed()).with_output(output));
            }
            Ok(ClaudePluginOperationResult::new(status::SUCCEEDED, shared::marketplace_refresh_succeeded()).with_output(output))
        };
        work.await.unwrap_or_else(Stop::operation)
    }

    /// The automatic update (PluginAutoUpdates.cs): `plugin marketplace upgrade --json` once, when a registered Git marketplace
    /// exists; the exit code decides, the JSON's `errors` can still fail it.
    pub async fn upgrade_marketplaces(&self, workspace: &Workspace, cancel: &CancellationToken) -> PluginAutoUpdateResult {
        let _operating = match self.core.begin_auto_update(cancel) {
            Ok(operating) => operating,
            Err(refused) => return refused,
        };
        let core = &self.core;
        let work = async {
            let cwd = core.local_directory(workspace.path())?;
            let (executable, version) = Self::command(core, &cwd, cancel).await?;
            let snapshot = Self::list(core, &executable, &cwd, &version, cancel).await?;
            cancelled(cancel)?;
            if snapshot.status != status::READY {
                let kind = if matches!(snapshot.status.as_str(), status::MISSING | status::UNSUPPORTED) { "skipped" } else { "failed" };
                return Ok(PluginAutoUpdateResult::new(kind, snapshot.detail));
            }
            if !snapshot.marketplaces.iter().any(|m| m.source_kind == "git") {
                return Ok(PluginAutoUpdateResult::new("skipped", auto_strings::no_git_marketplaces()));
            }
            let call = core.call(&executable, &["plugin", "marketplace", "upgrade", "--json"], &cwd, OPERATION_TIMEOUT);
            let result = match core.runner.run(call, cancel.clone()).await {
                Ok(result) => result,
                Err(RunError::Cancelled) => return Err(Stop::Cancelled),
                Err(RunError::Failed(_)) => return Ok(PluginAutoUpdateResult::failure()),
            };
            cancelled(cancel)?;
            if result.exit_code != 0 || result.timed_out || result.output.len() > 1_048_576 {
                return Ok(PluginAutoUpdateResult::failure());
            }
            let (mut selected, mut errors) = (Vec::new(), 0);
            // The exit code is authoritative; a newer CLI may leave the JSON detail out.
            if let Ok(Value::Object(root)) = serde_json::from_str::<Value>(&result.output) {
                if let Some(Value::Array(rows)) = root.get("selectedMarketplaces") {
                    selected = rows.iter().filter_map(Value::as_str).map(|row| wire::clean(row, 160)).take(128).collect();
                }
                if let Some(Value::Array(values)) = root.get("errors") {
                    errors = values.len();
                }
            }
            Ok(if errors > 0 {
                PluginAutoUpdateResult {
                    failed: Some(selected),
                    ..PluginAutoUpdateResult::new("failed", auto_strings::count("pluginAutoUpdate.marketplaceErrorsTemplate", errors))
                }
            } else {
                let detail = if selected.is_empty() {
                    auto_strings::marketplaces_upgraded()
                } else {
                    auto_strings::count("pluginAutoUpdate.marketplacesTemplate", selected.len())
                };
                PluginAutoUpdateResult { updated: Some(selected), ..PluginAutoUpdateResult::new("succeeded", detail) }
            })
        };
        work.await.unwrap_or_else(Stop::auto_update)
    }
}

fn ready(result: ClaudePluginOperationResult) -> BoxFuture<'static, ClaudePluginOperationResult> {
    Box::pin(async move { result })
}

impl PluginReader for CodexPluginReader {
    fn snapshot(&self, workspace: &Workspace, cancel: CancellationToken) -> BoxFuture<'static, Arc<ClaudePluginSnapshot>> {
        let path = workspace.path().to_string();
        self.core.snapshot(move |core| Box::pin(Self::read(core, path, cancel)))
    }

    fn shutdown(&self) {
        self.core.shutdown();
    }

    /// `plugin add <id> --json`. Codex installs at user level only, so any other scope is refused before a snapshot is read.
    fn install(
        &self,
        plugin_id: &str,
        scope: &str,
        workspace: &Workspace,
        cancel: CancellationToken,
    ) -> BoxFuture<'static, ClaudePluginOperationResult> {
        if support::plugin_parts(Some(plugin_id)).is_none() || scope != "user" {
            return ready(ClaudePluginOperationResult::new(status::FAILED, shared::install_bad_id_or_scope()));
        }
        let operating = match self.core.begin_operation() {
            Ok(operating) => operating,
            Err(refused) => return ready(refused),
        };
        Box::pin(Self::run_operation(self.core.clone(), operating, Some(plugin_id.into()), None, workspace.path().to_string(), cancel))
    }

    /// `plugin marketplace upgrade <name> --json`, for a registered Git source.
    fn refresh_marketplace(
        &self,
        marketplace: &str,
        workspace: &Workspace,
        cancel: CancellationToken,
    ) -> BoxFuture<'static, ClaudePluginOperationResult> {
        if !support::identifier(Some(marketplace)) {
            return ready(ClaudePluginOperationResult::new(status::FAILED, shared::marketplace_bad_name()));
        }
        let operating = match self.core.begin_operation() {
            Ok(operating) => operating,
            Err(refused) => return ready(refused),
        };
        Box::pin(Self::run_operation(self.core.clone(), operating, None, Some(marketplace.into()), workspace.path().to_string(), cancel))
    }
}
