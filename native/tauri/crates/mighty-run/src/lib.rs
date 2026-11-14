//! Mighty Code's run engine: what starts an agent CLI for a pane, reads its stream and hands the pane what it said
//! (C# `MightyCode.Core`: RunManager.cs, ProviderCatalog.cs, ToolPermissions.cs, OutputParser.cs, ClaudePlanMode.cs, ModBridge.cs and
//! the run half of DesktopService.cs). No Tauri, no OS code: every child starts through `mighty_platform::ProcessHost`.
//!
//! | C# | here |
//! |---|---|
//! | `Models.cs` `StartRunRequest`, `RunEvent`, `ModelOption`/`ModelCatalog`/`ProviderRuntime`/`RuntimeInfo`, `RunSession.Apply` | [`model`], [`apply`] |
//! | `ProviderCatalog.cs` | [`providers`] |
//! | `KeptModelLists.cs` (`KeptModelLists`, `ModelListNotices`) | [`kept_lists`] |
//! | `ModelSelection.cs` (`ModelSelectionSupport`, `ModelCatalogRefresh`), `ClaudeModelReset.cs` | [`model_selection`] |
//! | `PhaseModelPreferences.cs`, `PhaseModelRouting.cs`, `PhaseModelSection.cs` (its data; W6 draws the screen) | [`phase_models`], [`phase_routing`], [`phase_section`] |
//! | `ModelSettingsFileStore.cs`, `OmcAgentCatalog.cs` | [`model_settings`] |
//! | `ToolPermissions.cs`, `ToolPermissionPresentation.cs`, `ToolPermissionStrings.cs` | [`permissions`], [`presentation`] |
//! | `CodexApprovalChannel.cs` | [`codex_approval`] |
//! | `UserQuestionnaire.cs`, `QuestionnaireProgress.cs`, `AgentQuestions.cs` | [`questionnaire`] |
//! | `OutputParser.cs` (Claude, Codex, pi), `ClaudeStream` | [`parser`], [`stream`] |
//! | `PiDialogChannel.cs` | [`pi_dialogs`] |
//! | `AgentActivity.cs` (`ActivitySupport`, `MetadataJson`) | [`activity`], [`meta`] |
//! | `SessionUsage.cs`, `AgentRunTiming.cs` | [`usage`], [`timing`] |
//! | `AgentHandback.cs`, `ClaudeResume.cs`, `RunLogText.cs`, `ContextCompaction.cs`, `BedrockSettings.RuntimeFailureGuidance`, `CliAuthFailure` | [`handback`], [`resume`], [`run_log`], [`compaction`], [`failure`] |
//! | `ClaudePlanMode.cs`, `FlowSteps.cs` (recording), `BackgroundOutputTail.cs` | [`plan_mode`], [`flow_steps`], [`background_output`] |
//! | `ModBridge.cs`, `HttpHost.cs` | [`mods`] |
//! | `AttachmentSupport.cs` | [`attachments`] |
//! | `RunManager.cs` | [`manager`] (with the [`graph::GraphSink`] the graph tracker plugs into) |
//! | `DesktopService.cs` (runs) | [`service`] |
//! | `AntigravityPermissions.cs`, `AntigravityRecords.cs` (the records' helpers), `AntigravityMigration.ModelUnavailable` | [`antigravity`] |

pub mod activity;
pub mod antigravity;
pub mod apply;
pub mod attachments;
pub mod background_output;
pub mod codex_approval;
pub mod compaction;
pub mod failure;
pub mod flow_steps;
pub mod graph;
pub mod handback;
pub mod kept_lists;
pub mod lines;
pub mod manager;
pub mod meta;
pub mod model;
pub mod model_selection;
pub mod model_settings;
pub mod mods;
pub mod os;
pub mod parser;
pub mod permissions;
pub mod phase_models;
pub mod phase_routing;
pub mod phase_section;
pub mod pi_dialogs;
pub mod plan_mode;
pub mod presentation;
pub mod providers;
pub mod questionnaire;
pub mod resume;
pub mod run_log;
pub mod service;
pub mod stream;
pub mod strings;
pub mod text_json;
pub mod timing;
pub mod usage;

pub use graph::{GraphFactory, GraphSink, GraphStart};
pub use manager::{RunManager, RunManagerOptions};
pub use model::{RunEvent, StartRunRequest};
pub use service::{RunService, ServiceEvent};

/// A failure a caller shows as it is: C# throws `ArgumentException`, `InvalidOperationException`, `TimeoutException`,
/// `OperationCanceledException`, `IOException`; the message is the user's text (from the locale catalogues where C# reads them).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RunError {
    /// `ArgumentException`: the request or answer is not one this takes.
    Invalid(String),
    /// `InvalidOperationException`: not now (already settled, already running, no such run).
    State(String),
    /// `TimeoutException`.
    TimedOut(String),
    /// `OperationCanceledException`.
    Cancelled(String),
    /// `IOException` and the OS's other failures.
    Io(String),
}

impl RunError {
    pub fn message(&self) -> &str {
        match self {
            RunError::Invalid(m) | RunError::State(m) | RunError::TimedOut(m) | RunError::Cancelled(m) | RunError::Io(m) => m,
        }
    }
}

impl std::fmt::Display for RunError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.message())
    }
}

impl std::error::Error for RunError {}

impl From<mighty_platform::PlatformError> for RunError {
    fn from(error: mighty_platform::PlatformError) -> Self {
        RunError::Io(error.to_string())
    }
}

pub type Result<T> = std::result::Result<T, RunError>;

pub(crate) fn lock<T>(mutex: &std::sync::Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}
