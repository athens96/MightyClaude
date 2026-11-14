//! The plugin list of a workspace as both readers answer it (C# ClaudePlugins.cs models). Provider-neutral on purpose: the Codex
//! list and the marketplace feature reuse them unchanged (docs/windows-plugins.md).

use std::sync::Arc;

use mighty_core::model::Workspace;
use mighty_platform::BoxFuture;

use crate::runner::CancellationToken;

/// The status words of a snapshot (ready, missing, unsupported, failed, cancelled) and of an operation (succeeded, skipped, busy
/// besides); C# `ClaudePluginStatus`.
pub mod status {
    pub const READY: &str = "ready";
    pub const MISSING: &str = "missing";
    pub const UNSUPPORTED: &str = "unsupported";
    pub const FAILED: &str = "failed";
    pub const CANCELLED: &str = "cancelled";
    pub const SUCCEEDED: &str = "succeeded";
    pub const SKIPPED: &str = "skipped";
    pub const BUSY: &str = "busy";
}

/// CLI settings state for this working folder (C# `ClaudeInstalledPlugin`), not proof that a running CLI loaded the plugin.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClaudeInstalledPlugin {
    pub plugin_id: String,
    pub name: String,
    pub marketplace: Option<String>,
    pub version: Option<String>,
    pub scope: String,
    pub enabled: Option<bool>,
    pub project_path: Option<String>,
    pub description: String,
    pub errors: Vec<String>,
    pub notes: Vec<String>,
}

impl Default for ClaudeInstalledPlugin {
    fn default() -> Self {
        ClaudeInstalledPlugin {
            plugin_id: String::new(),
            name: String::new(),
            marketplace: None,
            version: None,
            scope: "user".into(),
            enabled: None,
            project_path: None,
            description: String::new(),
            errors: Vec::new(),
            notes: Vec::new(),
        }
    }
}

impl ClaudeInstalledPlugin {
    /// The row's key, length-prefixed (UTF-8 bytes) so no name, scope or path can forge another row's key.
    pub fn id(&self) -> String {
        [self.plugin_id.as_str(), self.scope.as_str(), self.project_path.as_deref().unwrap_or("")]
            .iter()
            .map(|value| format!("{}:{value}", value.len()))
            .collect::<Vec<_>>()
            .join("|")
    }
}

/// One plugin a registered marketplace offers (C# `ClaudeCatalogPlugin`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClaudeCatalogPlugin {
    pub id: String,
    pub name: String,
    pub description: String,
    pub marketplace: String,
    pub version: Option<String>,
    pub source_kind: String,
}

impl Default for ClaudeCatalogPlugin {
    fn default() -> Self {
        ClaudeCatalogPlugin {
            id: String::new(),
            name: String::new(),
            description: String::new(),
            marketplace: String::new(),
            version: None,
            source_kind: "unknown".into(),
        }
    }
}

/// A registered marketplace (C# `ClaudePluginMarketplace`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClaudePluginMarketplace {
    pub name: String,
    pub source_kind: String,
}

impl ClaudePluginMarketplace {
    pub fn new(name: &str, source_kind: &str) -> ClaudePluginMarketplace {
        ClaudePluginMarketplace { name: name.into(), source_kind: source_kind.into() }
    }
}

/// One read (C# `ClaudePluginSnapshot`). A default snapshot is never ready.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClaudePluginSnapshot {
    pub status: String,
    pub detail: String,
    pub cli_version: Option<String>,
    pub installed: Vec<ClaudeInstalledPlugin>,
    pub available: Vec<ClaudeCatalogPlugin>,
    pub marketplaces: Vec<ClaudePluginMarketplace>,
    pub updated_at: Option<String>,
    /// Bounded CLI output, only shown behind the diagnostics disclosure.
    pub diagnostic_output: String,
}

impl Default for ClaudePluginSnapshot {
    fn default() -> Self {
        ClaudePluginSnapshot {
            status: status::FAILED.into(),
            detail: String::new(),
            cli_version: None,
            installed: Vec::new(),
            available: Vec::new(),
            marketplaces: Vec::new(),
            updated_at: None,
            diagnostic_output: String::new(),
        }
    }
}

impl ClaudePluginSnapshot {
    /// A snapshot with only a status and its sentence.
    pub fn of(status: &str, detail: impl Into<String>) -> ClaudePluginSnapshot {
        ClaudePluginSnapshot { status: status.into(), detail: detail.into(), ..ClaudePluginSnapshot::default() }
    }
}

/// What an install or a marketplace refresh reports (C# `ClaudePluginOperationResult`): the macOS status word, the macOS sentence
/// and the bounded CLI output shown only on request.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClaudePluginOperationResult {
    pub status: String,
    pub detail: String,
    pub output: String,
}

impl ClaudePluginOperationResult {
    pub fn new(status: &str, detail: impl Into<String>) -> ClaudePluginOperationResult {
        ClaudePluginOperationResult { status: status.into(), detail: detail.into(), output: String::new() }
    }

    pub fn with_output(mut self, output: impl Into<String>) -> ClaudePluginOperationResult {
        self.output = output.into();
        self
    }
}

/// C# `IPluginReader`: the reads and the two mutations macOS has, and no others — install one available plugin, refresh one
/// registered marketplace. There is no uninstall, enable, disable or marketplace add. Each call decides whether it may start when
/// it is made (one read at a time, one operation at a time); the future it returns does the work.
pub trait PluginReader: Send + Sync {
    /// The workspace's installed and available plugins; a second call while a read runs is answered from that read.
    fn snapshot(&self, workspace: &Workspace, cancel: CancellationToken) -> BoxFuture<'static, Arc<ClaudePluginSnapshot>>;
    /// Closing the window stops admitting reads and operations. Nothing is rolled back, because reading changed nothing.
    fn shutdown(&self);
    fn install(
        &self,
        plugin_id: &str,
        scope: &str,
        workspace: &Workspace,
        cancel: CancellationToken,
    ) -> BoxFuture<'static, ClaudePluginOperationResult>;
    fn refresh_marketplace(
        &self,
        marketplace: &str,
        workspace: &Workspace,
        cancel: CancellationToken,
    ) -> BoxFuture<'static, ClaudePluginOperationResult>;
}
