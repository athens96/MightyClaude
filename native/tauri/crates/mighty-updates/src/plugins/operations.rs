//! C# `PluginOperations`: what the running app owns so the two plugin mutations macOS has — install one plugin, refresh the
//! registered marketplaces — and the automatic plugin update really run. It holds the one shared runner every plugin read and
//! mutation goes through, and the app-wide one-at-a-time gate: the Claude and the Codex window are two browsers, and macOS runs one
//! plugin operation at a time for the whole app. A second operation is refused with the macOS sentence instead of starting a CLI.
//!
//! Also `PluginAutoUpdateResult` (CliAutoUpdateSchedule.cs).

use std::collections::BTreeMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

use mighty_core::model::Workspace;
use mighty_platform::{ProcessHost, ShellEnvironment};
use serde_json::Value;

use super::browser::ClaudePluginBrowser;
use super::claude::ClaudePluginReader;
use super::codex::CodexPluginReader;
use super::model::{ClaudePluginOperationResult, PluginReader, status};
use super::strings::{auto_update as auto_strings, plugin as strings};
use super::support;
use crate::runner::{CancellationToken, CliRunResult, CliRunner, HostCliRunner};

/// What one automatic plugin update reports (C# `PluginAutoUpdateResult`): succeeded, failed, skipped, busy or cancelled, its
/// sentence, and the plugins (or Codex marketplaces) updated, needing the user's approval, or failed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginAutoUpdateResult {
    pub status: String,
    pub detail: String,
    pub updated: Option<Vec<String>>,
    pub needs_approval: Option<Vec<String>>,
    pub failed: Option<Vec<String>>,
}

impl PluginAutoUpdateResult {
    pub fn new(status: &str, detail: impl Into<String>) -> PluginAutoUpdateResult {
        PluginAutoUpdateResult { status: status.into(), detail: detail.into(), updated: None, needs_approval: None, failed: None }
    }

    pub fn busy() -> PluginAutoUpdateResult {
        Self::new("busy", auto_strings::busy())
    }

    pub fn cancelled() -> PluginAutoUpdateResult {
        Self::new("cancelled", auto_strings::cancelled())
    }

    pub fn failure() -> PluginAutoUpdateResult {
        Self::new("failed", auto_strings::failed())
    }

    /// How one `claude plugin update … --json` went: "needsApproval" when the marketplace showed a command (never approved here),
    /// else the JSON's outcome with the exit code, else the exit code. A timeout or more than 1 MiB of output failed.
    pub fn claude_outcome(result: &CliRunResult) -> &'static str {
        if result.timed_out || result.output.len() > 1_048_576 {
            return "failed";
        }
        let last = result.output.split('\n').rfind(|line| !line.trim().is_empty()).unwrap_or("");
        if let Ok(Value::Object(root)) = serde_json::from_str::<Value>(last) {
            if root.contains_key("shownCommand") {
                return "needsApproval";
            }
            if let Some(outcome) = root.get("outcome").and_then(Value::as_str) {
                return if outcome == "ok" && result.exit_code == 0 { "updated" } else { "failed" };
            }
        }
        if result.exit_code == 0 { "updated" } else { "failed" }
    }
}

/// The reader of one provider, as its concrete type (C#'s `reader switch { ClaudePluginReader …, CodexPluginReader … }`).
#[derive(Clone)]
pub enum ProviderReader {
    Claude(ClaudePluginReader),
    Codex(CodexPluginReader),
}

impl ProviderReader {
    /// The reader as the window sees it: the reads and the two mutations.
    pub fn reader(&self) -> Arc<dyn PluginReader> {
        match self {
            ProviderReader::Claude(reader) => Arc::new(reader.clone()),
            ProviderReader::Codex(reader) => Arc::new(reader.clone()),
        }
    }

    pub fn shutdown(&self) {
        match self {
            ProviderReader::Claude(reader) => reader.shutdown(),
            ProviderReader::Codex(reader) => reader.shutdown(),
        }
    }
}

/// Makes the reader of a provider (a check's or the smoke's stand-in for the real ones).
pub type ReaderFactory = Arc<dyn Fn(&str) -> ProviderReader + Send + Sync>;

pub struct PluginOperations {
    runner: Arc<dyn CliRunner>,
    /// The CLI environment the app's readers are built with (C# `CliEnvironment`); None for a check's instance.
    environment: Option<Arc<dyn ShellEnvironment>>,
    running: AtomicBool,
}

/// Clears the gate when the operation ends or is dropped.
struct Running<'a>(&'a AtomicBool);

impl Drop for Running<'_> {
    fn drop(&mut self) {
        self.0.store(false, Ordering::SeqCst);
    }
}

impl PluginOperations {
    /// The running app's instance: the shared one-shot runner over the platform's processes, with the macOS listing cap, and the
    /// CLI environment the readers are given. Making it starts no process.
    pub fn installed(host: Arc<dyn ProcessHost>, environment: Arc<dyn ShellEnvironment>) -> PluginOperations {
        let runner = HostCliRunner::new(host).with_output_cap(support::MAXIMUM_LISTING_BYTES);
        PluginOperations { runner: Arc::new(runner), environment: Some(environment), running: AtomicBool::new(false) }
    }

    /// A check's instance: any runner, nothing installed is ever started.
    pub fn new(runner: Arc<dyn CliRunner>) -> PluginOperations {
        PluginOperations { runner, environment: None, running: AtomicBool::new(false) }
    }

    /// The one shared runner.
    pub fn runner(&self) -> Arc<dyn CliRunner> {
        self.runner.clone()
    }

    /// True only for the instance the running app makes for itself — the one that speaks to the installed CLI.
    pub fn uses_installed_cli(&self) -> bool {
        self.environment.is_some()
    }

    /// True while an operation this object started is still running.
    pub fn is_running(&self) -> bool {
        self.running.load(Ordering::SeqCst)
    }

    fn admit(&self) -> Option<Running<'_>> {
        self.running.compare_exchange(false, true, Ordering::SeqCst, Ordering::SeqCst).ok().map(|_| Running(&self.running))
    }

    fn reader_with(&self, provider: &str, environment: BTreeMap<String, String>) -> ProviderReader {
        if provider == ClaudePluginBrowser::CODEX_PROVIDER {
            ProviderReader::Codex(CodexPluginReader::new(self.runner.clone(), environment))
        } else {
            ProviderReader::Claude(ClaudePluginReader::new(self.runner.clone(), environment))
        }
    }

    /// The reader the provider's window reads and mutates through, built on the shared runner.
    pub fn reader(&self, provider: &str) -> ProviderReader {
        let environment = self.environment.as_ref().map(|environment| environment.current().values).unwrap_or_default();
        self.reader_with(provider, environment)
    }

    /// The automatic update of one provider's plugins (Claude: the user-scope plugins within `budget`; Codex: the registered Git
    /// marketplaces), under the same app-wide gate as the window's operations. `home` is the user's home folder as a workspace.
    pub async fn update_installed(
        &self,
        provider: &str,
        home: &Workspace,
        budget: Duration,
        cancel: &CancellationToken,
        reader_factory: Option<ReaderFactory>,
    ) -> PluginAutoUpdateResult {
        let Some(_running) = self.admit() else { return PluginAutoUpdateResult::busy() };
        if !matches!(provider, "claude" | "codex") {
            return PluginAutoUpdateResult::failure();
        }
        let reader = match reader_factory {
            Some(factory) => factory(provider),
            None => {
                let environment = match &self.environment {
                    Some(environment) => {
                        let refreshed = tokio::select! {
                            _ = cancel.cancelled() => None,
                            refreshed = environment.resolve(None, false) => Some(refreshed),
                        };
                        let Some(refreshed) = refreshed else { return PluginAutoUpdateResult::cancelled() };
                        refreshed.values
                    }
                    None => BTreeMap::new(),
                };
                self.reader_with(provider, environment)
            }
        };
        let result = match &reader {
            ProviderReader::Claude(claude) => claude.update_installed(home, budget, cancel).await,
            ProviderReader::Codex(codex) => codex.upgrade_marketplaces(home, cancel).await,
        };
        reader.shutdown();
        result
    }

    /// The install button's call: the browser assembles the arguments from the values the list just returned, runs them through
    /// the reader and reads the result back; this object only decides that it may start at all.
    pub async fn install(
        &self,
        browser: &ClaudePluginBrowser,
        reader: &Arc<dyn PluginReader>,
        plugin_id: &str,
        cancel: &CancellationToken,
    ) -> ClaudePluginOperationResult {
        let Some(_running) = self.admit() else { return busy() };
        browser.install(reader, plugin_id, cancel).await
    }

    /// 마켓플레이스 새로고침's call, by the same rule.
    pub async fn refresh_marketplaces(
        &self,
        browser: &ClaudePluginBrowser,
        reader: &Arc<dyn PluginReader>,
        cancel: &CancellationToken,
    ) -> ClaudePluginOperationResult {
        let Some(_running) = self.admit() else { return busy() };
        browser.refresh_marketplaces(reader, cancel).await
    }
}

fn busy() -> ClaudePluginOperationResult {
    ClaudePluginOperationResult::new(status::BUSY, strings::operation_busy())
}
