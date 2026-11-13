//! The run's records (C# Models.cs:422-513): `StartRunRequest` and its `Validate`, `RunEvent` and its `Valid`, and the model lists a
//! provider's CLI gives (`ModelOption`, `ModelCatalog`, `ProviderCapabilities`, `ProviderRuntime`, `RuntimeInfo`, `CliCommand`).

use std::sync::{Arc, Mutex};

use mighty_core::json::{JsonOut, Wire};
use mighty_core::model::{
    AgentActivity, BackgroundWork, LogEntry, PhaseModelsSnapshot, PlanRecord, RegisteredModelEntry, RunSettings, SessionUsage, TodoProgress,
};
use mighty_core::{graph::MightyGraphRun, normalize, time, wire};
use serde_json::Value;

use crate::activity;
use crate::attachments::{self, RunAttachment};
use crate::permissions::ToolPermissionRequest;
use crate::plan_mode::outcome;
use crate::strings;
use crate::{Result, RunError};

/// The ids a start made (C# `RunStartReceipt`): the user entry the transcript shows and the graph run whose block appears at once.
#[derive(Debug, Default)]
pub struct RunStartReceipt {
    pub entry_id: Option<String>,
    pub graph_run_id: Option<String>,
}

/// C# `StartRunRequest`. The fields after `attachments` are C#'s `[JsonIgnore]` ones: never read from a remote's JSON, never saved.
#[derive(Debug, Clone)]
pub struct StartRunRequest {
    pub session_id: String,
    pub workspace_id: String,
    /// "claude" (an agent pane) or "shell".
    pub kind: String,
    pub input: String,
    pub registered_models: Vec<RegisteredModelEntry>,
    pub model: String,
    pub provider: String,
    pub settings: Option<RunSettings>,
    pub resume_id: Option<String>,
    /// An empty list reads as none (C#'s `Attachments` getter).
    pub attachments: Option<Vec<RunAttachment>>,
    /// A local style's own tools, allowed at each request (`StyleRunPermissions.AutoAllows`).
    pub style_auto_allow: Option<Vec<String>>,
    pub phase_models: Option<PhaseModelsSnapshot>,
    /// This request alone starts in this mode (only "plan", only Claude).
    pub permission_mode_override: Option<String>,
    /// The pane's saved checklist, which a resumed Claude run continues.
    pub todo_progress: Option<TodoProgress>,
    /// A delegated child pane's worktree folder.
    pub working_folder: Option<String>,
    pub delegation_worktree_root: Option<String>,
    /// An Antigravity continuation's rules and card (W5): kept as JSON until that port.
    pub antigravity_grant: Option<Value>,
    pub antigravity_card: Option<Value>,
    /// The id this run's diagram block carries (`DesktopService.Launch` names it); None lets the runner pick one.
    pub graph_run_id: Option<String>,
    pub receipt: Option<Arc<Mutex<RunStartReceipt>>>,
}

impl StartRunRequest {
    /// `new StartRunRequest(sessionId, workspaceId, kind, input, registeredModels)` with C#'s defaults.
    pub fn new(session_id: &str, workspace_id: &str, kind: &str, input: &str) -> StartRunRequest {
        StartRunRequest {
            session_id: session_id.into(),
            workspace_id: workspace_id.into(),
            kind: kind.into(),
            input: input.into(),
            registered_models: vec![],
            model: "default".into(),
            provider: "claude".into(),
            settings: None,
            resume_id: None,
            attachments: None,
            style_auto_allow: None,
            phase_models: None,
            permission_mode_override: None,
            todo_progress: None,
            working_folder: None,
            delegation_worktree_root: None,
            antigravity_grant: None,
            antigravity_card: None,
            graph_run_id: None,
            receipt: None,
        }
    }

    pub fn with_provider(mut self, provider: &str) -> StartRunRequest {
        self.provider = provider.into();
        self
    }

    pub fn with_settings(mut self, settings: RunSettings) -> StartRunRequest {
        self.settings = Some(settings);
        self
    }

    /// `Attachments`: a list of none reads as none.
    pub fn attachment_list(&self) -> Option<&[RunAttachment]> {
        self.attachments.as_deref().filter(|a| !a.is_empty())
    }

    pub fn settings(&self) -> RunSettings {
        self.settings.clone().unwrap_or_default()
    }

    /// `StartRunRequest.Validate` (Models.cs:457-475): every rule a run start must keep, the request with its settings filled in.
    pub fn validate(&self) -> Result<StartRunRequest> {
        let settings = self.settings();
        let files = attachments::validate(self.attachment_list())?;
        let invalid = |key: &str| RunError::Invalid(strings::get(key));
        if !wire::identifier(&self.session_id)
            || !wire::identifier(&self.workspace_id)
            || !matches!(self.kind.as_str(), "claude" | "shell")
            || !wire::model(&self.model)
            || !wire::PROVIDERS.contains(&self.provider.as_str())
            || self.resume_id.as_deref().is_some_and(|r| !wire::session_identifier(r))
        {
            return Err(invalid("wire.startRun.invalidFormat"));
        }
        if self.input.trim().is_empty() && files.is_none() || self.input.encode_utf16().count() > 100_000 || self.input.contains('\0') {
            return Err(invalid("wire.startRun.emptyInput"));
        }
        if self.kind == "shell" && files.is_some() {
            return Err(invalid("wire.startRun.attachmentAiOnly"));
        }
        let budget_invalid = settings.max_budget_usd.is_some_and(|b| !b.is_finite() || b <= 0.0 || b > 10000.0);
        if settings.effort != "default" && !wire::run_efforts(&self.provider).contains(&settings.effort.as_str())
            || !matches!(settings.permission_mode.as_str(), "manual" | "plan" | "onRequest" | "acceptEdits" | "auto" | "fullAccess")
            || settings.max_turns.is_some_and(|t| !(1..=1000).contains(&t))
            || budget_invalid
            || !matches!(settings.web_search.as_str(), "default" | "disabled" | "cached" | "live")
            || settings.advisor.as_deref().is_some_and(|a| !wire::model(a))
        {
            return Err(invalid("wire.startRun.invalidSettings"));
        }
        if settings.permission_mode == "onRequest" && (self.provider != "codex" || self.kind != "claude") {
            return Err(invalid("wire.startRun.codexOnlyFeatures"));
        }
        if settings.permission_mode == "auto" && (self.kind != "claude" || self.provider != "claude") {
            return Err(invalid("wire.startRun.autoModeClaudeOnly"));
        }
        if let Some(mode) = &self.permission_mode_override
            && (mode != "plan" || self.kind != "claude" || self.provider != "claude")
        {
            return Err(invalid("plan.error.overrideClaudeOnly"));
        }
        if self.working_folder.is_some() && (self.kind != "claude" || !normalize::is_working_folder(self.working_folder.as_deref())) {
            return Err(invalid("wire.startRun.invalidFormat"));
        }
        if (self.provider != "codex" || self.kind != "claude")
            && (settings.fast_mode || settings.web_search != "default" || settings.network_access)
            || settings.network_access && !matches!(settings.permission_mode.as_str(), "acceptEdits" | "onRequest")
        {
            return Err(invalid("wire.startRun.codexOnlyFeatures"));
        }
        if self.kind == "claude"
            && (self.provider != "claude" && (settings.max_turns.is_some() || settings.max_budget_usd.is_some())
                || self.provider == "codex" && settings.permission_mode == "plan"
                || self.provider == "gemini" && settings.effort != "default")
        {
            return Err(invalid("wire.startRun.unsupportedSettings"));
        }
        let grants = self.style_auto_allow.as_ref().filter(|g| !g.is_empty());
        if let Some(grants) = grants
            && (self.provider != "claude" || self.kind != "claude" || grants.len() > 32 || grants.iter().any(|g| !valid_wire_name(g)))
        {
            return Err(RunError::Invalid("Invalid local style tool permissions.".into()));
        }
        Ok(StartRunRequest { settings: Some(settings), attachments: files, style_auto_allow: grants.cloned(), ..self.clone() })
    }
}

/// `StyleRunPermissions.ValidWireName`: `ToolSearch`, or a plugin's MCP tool `mcp__plugin_<server>__<tool>`.
pub fn valid_wire_name(name: &str) -> bool {
    if name == "ToolSearch" {
        return true;
    }
    let token = |part: &str| (1..=64).contains(&part.len()) && part.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_' || b == b'-');
    let parts: Vec<&str> = name.split("__").collect();
    parts.len() == 3
        && parts[0] == "mcp"
        && parts[1].starts_with("plugin_")
        && !parts[1].ends_with('_')
        && token(parts[1])
        && parts[2] != "AskUserQuestion"
        && !parts[2].starts_with('_')
        && token(parts[2])
}

/// C# `RunEvent`: what a run tells its pane. `kind` is C#'s `Type`.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct RunEvent {
    pub session_id: String,
    pub kind: String,
    pub entry: Option<LogEntry>,
    pub status: Option<String>,
    pub resume_id: Option<String>,
    pub activity: Option<AgentActivity>,
    pub usage: Option<SessionUsage>,
    pub permission: Option<ToolPermissionRequest>,
    pub graph_run: Option<MightyGraphRun>,
    pub reason: Option<String>,
    pub plan: Option<PlanRecord>,
    pub todos: Option<TodoProgress>,
    pub background: Option<BackgroundWork>,
    /// An Antigravity turn's refused actions (W5), as JSON until that port.
    pub antigravity_permission: Option<Value>,
}

impl RunEvent {
    pub fn new(session_id: &str, kind: &str) -> RunEvent {
        RunEvent { session_id: session_id.into(), kind: kind.into(), ..RunEvent::default() }
    }

    /// `RunEvent.Log`: a new entry, its text cleaned to 128 KiB (an answer) or 32 KiB.
    pub fn log(session_id: &str, kind: &str, text: &str, provider: Option<&str>) -> RunEvent {
        let limit = if kind == "assistant" { activity::MAXIMUM_MESSAGE_BYTES } else { 32768 };
        let entry =
            LogEntry::new(wire::new_id(), kind, activity::clean(Some(text), limit, false), time::now_o(), provider.map(str::to_string));
        RunEvent { entry: Some(entry), ..RunEvent::new(session_id, "log") }
    }

    /// `RunEvent.Log` with an entry made elsewhere.
    pub fn entry(session_id: &str, entry: LogEntry) -> RunEvent {
        RunEvent { entry: Some(entry), ..RunEvent::new(session_id, "log") }
    }

    /// `RunEvent.State`.
    pub fn state(session_id: &str, state: &str, reason: Option<&str>) -> RunEvent {
        RunEvent { status: Some(state.into()), reason: reason.map(str::to_string), ..RunEvent::new(session_id, "status") }
    }

    /// `RunEvent.Valid`: an event the state may apply.
    pub fn valid(&self) -> bool {
        if !wire::identifier(&self.session_id) {
            return false;
        }
        match self.kind.as_str() {
            "status" => matches!(self.status.as_deref(), Some("idle" | "running" | "completed" | "error" | "stopped")),
            "resume" => self.resume_id.as_deref().is_some_and(wire::session_identifier),
            "log" => self.entry.as_ref().is_some_and(|entry| {
                let limit = if entry.kind() == "assistant" { activity::MAXIMUM_MESSAGE_BYTES } else { 32768 };
                wire::is_identifier(entry.id.as_deref())
                    && mighty_core::model::STORED_KINDS.contains(&entry.kind())
                    && (entry.kind() != "image" || normalize::images(entry.images.as_ref()).is_some())
                    && entry.text.as_deref().is_some_and(|t| t.len() <= limit)
            }),
            "activity" => self.activity.as_ref().is_some_and(|a| activity::normalized(a, false).is_some()),
            "usage" => true,
            "graph_run" => self.graph_run.as_ref().is_some_and(|run| wire::identifier(run.id())),
            "plan" => self
                .plan
                .as_ref()
                .is_some_and(|p| wire::is_identifier(p.id.as_deref()) && p.outcome.as_deref().is_some_and(|o| outcome::ALL.contains(&o))),
            "todos" => self.todos.is_some(),
            "background" => self.background.is_some(),
            // RemoteWorkspaceValidation.AntigravityCard is W5's: until then a card is valid when it is an object.
            "antigravityPermission" => self.antigravity_permission.as_ref().is_some_and(Value::is_object),
            _ => false,
        }
    }

    /// As `Wire.Json` writes the record (null fields left out).
    pub fn to_json(&self) -> String {
        let mut out = JsonOut::new();
        out.begin_object();
        out.prop_str("sessionId", Some(&self.session_id));
        out.prop_str("type", Some(&self.kind));
        out.prop("entry", self.entry.as_ref());
        out.prop_str("status", self.status.as_deref());
        out.prop_str("resumeId", self.resume_id.as_deref());
        out.prop("activity", self.activity.as_ref());
        out.prop("usage", self.usage.as_ref());
        if let Some(permission) = &self.permission {
            out.name("permission");
            permission.write(&mut out);
        }
        out.prop("graphRun", self.graph_run.as_ref());
        out.prop_str("reason", self.reason.as_deref());
        out.prop("plan", self.plan.as_ref());
        out.prop("todos", self.todos.as_ref());
        out.prop("background", self.background.as_ref());
        if let Some(card) = &self.antigravity_permission {
            out.name("antigravityPermission");
            out.value(card);
        }
        out.end_object();
        out.finish()
    }
}

/// C# `ModelOption(Value, DisplayName, Description, ResolvedModel?, SupportsEffort?, SupportedEffortLevels?)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModelOption {
    pub value: String,
    pub display_name: String,
    pub description: String,
    pub resolved_model: Option<String>,
    pub supports_effort: Option<bool>,
    pub supported_effort_levels: Option<Vec<String>>,
}

impl ModelOption {
    pub fn new(value: &str, display_name: &str, description: &str) -> ModelOption {
        ModelOption {
            value: value.into(),
            display_name: display_name.into(),
            description: description.into(),
            resolved_model: None,
            supports_effort: None,
            supported_effort_levels: None,
        }
    }

    pub fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("value", Some(&self.value));
        out.prop_str("displayName", Some(&self.display_name));
        out.prop_str("description", Some(&self.description));
        out.prop_str("resolvedModel", self.resolved_model.as_deref());
        out.prop_opt_bool("supportsEffort", self.supports_effort);
        out.prop_strings("supportedEffortLevels", self.supported_effort_levels.as_ref());
        out.end_object();
    }

    /// The label rules' view of it (mighty-core `model_label`).
    pub fn label_option(&self) -> mighty_core::model_label::ModelOption {
        mighty_core::model_label::ModelOption {
            value: self.value.clone(),
            display_name: self.display_name.clone(),
            resolved_model: self.resolved_model.clone(),
        }
    }
}

/// C# `ModelCatalog(Source, Models, Detail, ReadFailure?, ListedAt?)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModelCatalog {
    pub source: String,
    pub models: Vec<ModelOption>,
    pub detail: String,
    pub read_failure: Option<String>,
    pub listed_at: Option<String>,
}

impl ModelCatalog {
    pub fn new(source: &str, models: Vec<ModelOption>, detail: &str) -> ModelCatalog {
        ModelCatalog { source: source.into(), models, detail: detail.into(), read_failure: None, listed_at: None }
    }

    /// `IsFresh`: the list the CLI gave on its latest read.
    pub fn is_fresh(&self) -> bool {
        self.source == "cli" && self.read_failure.is_none()
    }

    pub fn with_failure(mut self, failure: String) -> ModelCatalog {
        self.read_failure = Some(failure);
        self
    }

    pub fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("source", Some(&self.source));
        out.name("models");
        out.begin_array();
        for model in &self.models {
            model.write(out);
        }
        out.end_array();
        out.prop_str("detail", Some(&self.detail));
        out.prop_str("readFailure", self.read_failure.as_deref());
        out.prop_str("listedAt", self.listed_at.as_deref());
        out.end_object();
    }
}

/// C# `ProviderCapabilities`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProviderCapabilities {
    pub effort: bool,
    pub permission_modes: Vec<String>,
    pub max_turns: bool,
    pub max_budget_usd: bool,
    pub resume: bool,
    pub fast_mode: bool,
    pub web_search: bool,
    pub network_access: bool,
    pub attachments: bool,
}

/// C# `ProviderRuntime`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProviderRuntime {
    pub id: String,
    pub name: String,
    pub available: bool,
    pub version: Option<String>,
    pub detail: String,
    pub model_catalog: ModelCatalog,
    pub capabilities: ProviderCapabilities,
}

/// C# `ModsInfo`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModsInfo {
    pub status: String,
    pub minimum_version: String,
    pub detail: String,
}

/// C# `RuntimeInfo`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RuntimeInfo {
    pub platform: String,
    pub app_version: String,
    pub claude_available: bool,
    pub claude_version: Option<String>,
    pub model_catalog: Option<ModelCatalog>,
    pub providers: Vec<ProviderRuntime>,
    pub mods: Option<ModsInfo>,
}

/// C# `CliCommand(Binary, Prefix, Version?)`: a CLI found, the arguments it always takes first, and what `--version` said.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CliCommand {
    pub binary: String,
    pub prefix: Vec<String>,
    pub version: Option<String>,
}

impl CliCommand {
    pub fn new(binary: impl Into<String>, prefix: Vec<String>, version: Option<&str>) -> CliCommand {
        CliCommand { binary: binary.into(), prefix, version: version.map(str::to_string) }
    }
}

/// `RegisteredModelEntry.Name` as C# reads it (never null there).
pub fn registered_name(entry: &RegisteredModelEntry) -> &str {
    entry.name.as_deref().unwrap_or("")
}

/// The JSON of a record C# serializes with `Wire.Json`.
pub fn wire_json<T: Wire>(value: &T) -> String {
    value.to_json()
}
