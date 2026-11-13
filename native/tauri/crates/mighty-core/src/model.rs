//! The saved state, `workspace-state.json` version 1: C# `AppSnapshot`, `Workspace`, `RunSession`, `RunSettings`, `LogEntry` and the
//! records they hold (MightyCode.Core/Models.cs, AgentActivity.cs, AgentImages.cs, AgentRunTiming.cs, SessionUsage.cs,
//! ClaudePlanMode.cs, ShellBlockRuns.cs, PaneLayout.cs, Remote/MobileRelayHost.cs), read and written exactly as `Wire.Json` reads and
//! writes them (`json.rs`). The graph records are in `graph.rs`.
//!
//! Each field is the C# property, in the C# declaration order (the order `Wire.Json` writes). A C# reference-typed property is an
//! `Option` (None is C# null, left out when written); an absent property takes the C# initializer's value when read. C# keeps no
//! field it does not model (no `JsonExtensionData` anywhere in Core), so neither does this: a save drops unknown fields as C# does.
//! The converters C# puts on a field (lenient ones load damage as nothing) are kept field by field.

use std::sync::Arc;

use indexmap::IndexMap;
use serde_json::Value;

use crate::graph::{GraphBlockSize, MightyGraphRun};
use crate::json::{self, JsonOut, Obj, R, Wire, fail};
use crate::time::{self, DateTimeOffset};
use crate::wire::new_id;

/// A `Dictionary<string, string>` as read: each key once, a null value kept.
pub type StringMap = IndexMap<String, Option<String>>;

fn some(text: &str) -> impl FnOnce() -> Option<String> + '_ {
    move || Some(text.to_string())
}

// ── AppSnapshot ─────────────────────────────────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, PartialEq)]
pub struct AppSnapshot {
    pub version: i32,
    pub workspaces: Option<Vec<Workspace>>,
    pub sessions: Option<Vec<Arc<RunSession>>>,
    pub active_workspace_id: Option<String>,
    pub active_session_id: Option<String>,
    /// Null opens the active workspace only; `[]` means all closed (`WorkspaceDisclosureConverter`: anything but an array of strings
    /// reads as null).
    pub expanded_workspace_ids: Option<Vec<String>>,
    pub layout: Option<String>,
    pub pane_layouts: Option<IndexMap<String, Option<PaneLayoutNode>>>,
    pub pane_layout_modes: Option<StringMap>,
    pub pane_layout_active_session_ids: Option<StringMap>,
    pub theme: Option<String>,
    pub sidebar_width: f64,
    pub sidebar_collapsed: bool,
    pub trusted_status_lines: Option<StringMap>,
    pub status_line_enabled: bool,
    pub completion_notifications_enabled: bool,
    /// `LenientNullableBoolConverter`: anything but true or false reads as null.
    pub auto_update_clis: Option<bool>,
    pub auto_update_plugins: Option<bool>,
    pub auto_login_clis: Option<bool>,
    pub background_work_open: Option<bool>,
    pub last_create_location: Option<String>,
    pub antigravity_migrated: Option<bool>,
    pub reference_bubble_width: Option<f64>,
    pub reference_bubble_height: Option<f64>,
    pub claude_direct_usage_lookup_enabled: bool,
    pub app_update_auto_check: bool,
    pub app_update_last_checked_at: Option<String>,
    pub app_update_manifest_url_override: Option<String>,
    pub language_preference: Option<String>,
    pub settings_pane: Option<String>,
    pub agent_web_open_choices: Option<StringMap>,
    pub mobile_remote: Option<MobileRemoteSettings>,
    pub model_defaults: Option<ModelDefaultsConfig>,
    pub phase_models: Option<PhaseModelsSnapshot>,
    pub browser_engine_enabled: bool,
}

impl Default for AppSnapshot {
    /// `new AppSnapshot()`.
    fn default() -> Self {
        AppSnapshot {
            version: 1,
            workspaces: Some(vec![]),
            sessions: Some(vec![]),
            active_workspace_id: None,
            active_session_id: None,
            expanded_workspace_ids: None,
            layout: Some("grid".into()),
            pane_layouts: None,
            pane_layout_modes: None,
            pane_layout_active_session_ids: None,
            theme: Some("dark".into()),
            sidebar_width: 252.0,
            sidebar_collapsed: false,
            trusted_status_lines: None,
            status_line_enabled: true,
            completion_notifications_enabled: true,
            auto_update_clis: None,
            auto_update_plugins: None,
            auto_login_clis: None,
            background_work_open: None,
            last_create_location: None,
            antigravity_migrated: None,
            reference_bubble_width: None,
            reference_bubble_height: None,
            claude_direct_usage_lookup_enabled: false,
            app_update_auto_check: true,
            app_update_last_checked_at: None,
            app_update_manifest_url_override: None,
            language_preference: Some("system".into()),
            settings_pane: Some("general".into()),
            agent_web_open_choices: None,
            mobile_remote: Some(MobileRemoteSettings::default()),
            model_defaults: None,
            phase_models: None,
            browser_engine_enabled: false,
        }
    }
}

/// `LenientNullableBoolConverter.Read`.
fn lenient_bool(value: Option<&Value>) -> Option<bool> {
    value.and_then(Value::as_bool)
}

/// `LenientNullableDoubleConverter.Read`: a finite number, else null.
fn lenient_double(value: Option<&Value>) -> Option<f64> {
    value.filter(|v| v.is_number()).and_then(Value::as_f64).filter(|n| n.is_finite())
}

/// `LenientJsonConverter<string>.Read`: a string, else null.
fn lenient_string(value: Option<&Value>) -> Option<String> {
    value.and_then(Value::as_str).map(str::to_string)
}

/// `LenientNullableDoubleConverter.Write`: a number when finite, else a null literal (a null is never handed to it).
fn write_lenient_double(out: &mut JsonOut, name: &str, value: Option<f64>) {
    if let Some(number) = value {
        out.name(name);
        if number.is_finite() { out.float(number) } else { out.null() }
    }
}

impl Wire for AppSnapshot {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "the state")?;
        let expanded = match o.get("expandedWorkspaceIds") {
            Some(Value::Array(items)) if items.iter().all(Value::is_string) => {
                Some(items.iter().filter_map(Value::as_str).map(str::to_string).collect())
            }
            _ => None,
        };
        Ok(AppSnapshot {
            version: json::int32(o.get("version"), 1)?,
            workspaces: match json::list(o.get("workspaces"), Workspace::read)? {
                // A null workspace never reaches a reader: the load leaves out every workspace that is no object.
                Some(items) => Some(items.into_iter().flatten().collect()),
                None if o.get("workspaces").is_none() => Some(vec![]),
                None => None,
            },
            sessions: match json::list(o.get("sessions"), RunSession::read)? {
                Some(items) => Some(items.into_iter().flatten().map(Arc::new).collect()),
                None if o.get("sessions").is_none() => Some(vec![]),
                None => None,
            },
            active_workspace_id: json::opt_string(o.get("activeWorkspaceId"))?,
            active_session_id: json::opt_string(o.get("activeSessionId"))?,
            expanded_workspace_ids: expanded,
            layout: json::string(o.get("layout"), some("grid"))?,
            pane_layouts: json::dictionary(o.get("paneLayouts"), PaneLayoutNode::read)?,
            pane_layout_modes: json::string_map(o.get("paneLayoutModes"))?,
            pane_layout_active_session_ids: json::string_map(o.get("paneLayoutActiveSessionIds"))?,
            theme: json::string(o.get("theme"), some("dark"))?,
            sidebar_width: json::float64(o.get("sidebarWidth"), 252.0)?,
            sidebar_collapsed: json::boolean(o.get("sidebarCollapsed"), false)?,
            trusted_status_lines: json::string_map(o.get("trustedStatusLines"))?,
            status_line_enabled: json::boolean(o.get("statusLineEnabled"), true)?,
            completion_notifications_enabled: json::boolean(o.get("completionNotificationsEnabled"), true)?,
            auto_update_clis: lenient_bool(o.get("autoUpdateCLIs")),
            auto_update_plugins: json::opt_boolean(o.get("autoUpdatePlugins"))?,
            auto_login_clis: lenient_bool(o.get("autoLoginCLIs")),
            background_work_open: lenient_bool(o.get("backgroundWorkOpen")),
            last_create_location: lenient_string(o.get("lastCreateLocation")),
            antigravity_migrated: lenient_bool(o.get("antigravityMigrated")),
            reference_bubble_width: lenient_double(o.get("referenceBubbleWidth")),
            reference_bubble_height: lenient_double(o.get("referenceBubbleHeight")),
            claude_direct_usage_lookup_enabled: json::boolean(o.get("claudeDirectUsageLookupEnabled"), false)?,
            app_update_auto_check: json::boolean(o.get("appUpdateAutoCheck"), true)?,
            app_update_last_checked_at: json::opt_string(o.get("appUpdateLastCheckedAt"))?,
            app_update_manifest_url_override: json::opt_string(o.get("appUpdateManifestUrlOverride"))?,
            language_preference: json::string(o.get("languagePreference"), some("system"))?,
            settings_pane: json::string(o.get("settingsPane"), some("general"))?,
            agent_web_open_choices: json::string_map(o.get("agentWebOpenChoices"))?,
            mobile_remote: match o.get("mobileRemote") {
                None => Some(MobileRemoteSettings::default()),
                value => json::record(value)?,
            },
            model_defaults: json::record(o.get("modelDefaults"))?,
            phase_models: json::record(o.get("phaseModels"))?,
            browser_engine_enabled: json::boolean(o.get("browserEngineEnabled"), false)?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        self.write_with(out, true, true);
    }
}

impl AppSnapshot {
    /// Written with or without its `sessions` (the bridge's state text leaves them out), and with or without the graph agents'
    /// output files (`WebDelta.PageSnapshot`: the page never gets them).
    pub fn write_with(&self, out: &mut JsonOut, sessions: bool, output_files: bool) {
        out.begin_object();
        out.prop_int("version", self.version as i64);
        out.prop_list("workspaces", self.workspaces.as_ref());
        if sessions && let Some(list) = &self.sessions {
            out.name("sessions");
            out.begin_array();
            for session in list {
                session.write_with(out, true, output_files);
            }
            out.end_array();
        }
        out.prop_str("activeWorkspaceId", self.active_workspace_id.as_deref());
        out.prop_str("activeSessionId", self.active_session_id.as_deref());
        out.prop_strings("expandedWorkspaceIds", self.expanded_workspace_ids.as_ref());
        out.prop_str("layout", self.layout.as_deref());
        if let Some(layouts) = &self.pane_layouts {
            out.name("paneLayouts");
            out.begin_object();
            for (key, node) in layouts {
                out.name(key);
                match node {
                    Some(node) => node.write(out),
                    None => out.null(),
                }
            }
            out.end_object();
        }
        out.prop_string_map("paneLayoutModes", self.pane_layout_modes.as_ref());
        out.prop_string_map("paneLayoutActiveSessionIds", self.pane_layout_active_session_ids.as_ref());
        out.prop_str("theme", self.theme.as_deref());
        out.prop_float("sidebarWidth", self.sidebar_width);
        out.prop_bool("sidebarCollapsed", self.sidebar_collapsed);
        out.prop_string_map("trustedStatusLines", self.trusted_status_lines.as_ref());
        out.prop_bool("statusLineEnabled", self.status_line_enabled);
        out.prop_bool("completionNotificationsEnabled", self.completion_notifications_enabled);
        out.prop_opt_bool("autoUpdateCLIs", self.auto_update_clis);
        out.prop_opt_bool("autoUpdatePlugins", self.auto_update_plugins);
        out.prop_opt_bool("autoLoginCLIs", self.auto_login_clis);
        out.prop_opt_bool("backgroundWorkOpen", self.background_work_open);
        out.prop_str("lastCreateLocation", self.last_create_location.as_deref());
        out.prop_opt_bool("antigravityMigrated", self.antigravity_migrated);
        write_lenient_double(out, "referenceBubbleWidth", self.reference_bubble_width);
        write_lenient_double(out, "referenceBubbleHeight", self.reference_bubble_height);
        out.prop_bool("claudeDirectUsageLookupEnabled", self.claude_direct_usage_lookup_enabled);
        out.prop_bool("appUpdateAutoCheck", self.app_update_auto_check);
        out.prop_str("appUpdateLastCheckedAt", self.app_update_last_checked_at.as_deref());
        out.prop_str("appUpdateManifestUrlOverride", self.app_update_manifest_url_override.as_deref());
        out.prop_str("languagePreference", self.language_preference.as_deref());
        out.prop_str("settingsPane", self.settings_pane.as_deref());
        out.prop_string_map("agentWebOpenChoices", self.agent_web_open_choices.as_ref());
        out.prop("mobileRemote", self.mobile_remote.as_ref());
        out.prop("modelDefaults", self.model_defaults.as_ref());
        out.prop("phaseModels", self.phase_models.as_ref());
        out.prop_bool("browserEngineEnabled", self.browser_engine_enabled);
        out.end_object();
    }

    pub fn workspaces(&self) -> &[Workspace] {
        self.workspaces.as_deref().unwrap_or(&[])
    }
    pub fn sessions(&self) -> &[Arc<RunSession>] {
        self.sessions.as_deref().unwrap_or(&[])
    }
    pub fn theme(&self) -> &str {
        self.theme.as_deref().unwrap_or("")
    }
    pub fn layout(&self) -> &str {
        self.layout.as_deref().unwrap_or("")
    }
    pub fn language_preference(&self) -> &str {
        self.language_preference.as_deref().unwrap_or("")
    }
    pub fn workspace(&self, id: &str) -> Option<&Workspace> {
        self.workspaces().iter().find(|w| w.id() == id)
    }
    pub fn session(&self, id: &str) -> Option<&Arc<RunSession>> {
        self.sessions().iter().find(|s| s.id() == id)
    }
    /// The ids of a workspace's panes, in the snapshot's order.
    pub fn pane_ids(&self, workspace_id: &str) -> Vec<String> {
        self.sessions().iter().filter(|s| s.workspace_id() == workspace_id).map(|s| s.id().to_string()).collect()
    }
    pub fn sessions_mut(&mut self) -> &mut Vec<Arc<RunSession>> {
        self.sessions.get_or_insert_with(Vec::new)
    }
    pub fn workspaces_mut(&mut self) -> &mut Vec<Workspace> {
        self.workspaces.get_or_insert_with(Vec::new)
    }
}

// ── Workspace ───────────────────────────────────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, PartialEq)]
pub struct Workspace {
    pub id: Option<String>,
    pub name: Option<String>,
    pub path: Option<String>,
    pub created_at: Option<String>,
    pub model_defaults: Option<ModelDefaultsConfig>,
    /// Set when the folder is on another computer.
    pub remote: Option<RemoteWorkspaceReference>,
}

impl Default for Workspace {
    /// `new Workspace()`: a new id and the time now.
    fn default() -> Self {
        Workspace {
            id: Some(new_id()),
            name: Some("Workspace".into()),
            path: Some(String::new()),
            created_at: Some(time::now_o()),
            model_defaults: None,
            remote: None,
        }
    }
}

impl Wire for Workspace {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a workspace")?;
        Ok(Workspace {
            id: json::string(o.get("id"), || Some(new_id()))?,
            name: json::string(o.get("name"), some("Workspace"))?,
            path: json::string(o.get("path"), some(""))?,
            created_at: json::string(o.get("createdAt"), || Some(time::now_o()))?,
            model_defaults: json::record(o.get("modelDefaults"))?,
            remote: json::record(o.get("remote"))?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("id", self.id.as_deref());
        out.prop_str("name", self.name.as_deref());
        out.prop_str("path", self.path.as_deref());
        out.prop_str("createdAt", self.created_at.as_deref());
        out.prop("modelDefaults", self.model_defaults.as_ref());
        out.prop("remote", self.remote.as_ref());
        out.end_object();
    }
}

impl Workspace {
    pub fn id(&self) -> &str {
        self.id.as_deref().unwrap_or("")
    }
    pub fn name(&self) -> &str {
        self.name.as_deref().unwrap_or("")
    }
    pub fn path(&self) -> &str {
        self.path.as_deref().unwrap_or("")
    }
    pub fn remote(&self) -> Option<&RemoteWorkspaceReference> {
        self.remote.as_ref()
    }
}

/// C# `RemoteWorkspaceReference(ConnectionId, WorkspaceId, HostName) { Version = 2 }`.
#[derive(Debug, Clone, PartialEq)]
pub struct RemoteWorkspaceReference {
    pub connection_id: Option<String>,
    pub workspace_id: Option<String>,
    pub host_name: Option<String>,
    pub version: i32,
}

impl Wire for RemoteWorkspaceReference {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a remote reference")?;
        Ok(RemoteWorkspaceReference {
            connection_id: json::opt_string(o.get("connectionId"))?,
            workspace_id: json::opt_string(o.get("workspaceId"))?,
            host_name: json::opt_string(o.get("hostName"))?,
            version: json::int32(o.get("version"), 2)?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("connectionId", self.connection_id.as_deref());
        out.prop_str("workspaceId", self.workspace_id.as_deref());
        out.prop_str("hostName", self.host_name.as_deref());
        out.prop_int("version", self.version as i64);
        out.end_object();
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct ModelDefaultsConfig {
    pub claude: Option<ProviderModeDefaults>,
    pub codex: Option<ProviderModeDefaults>,
}

impl Wire for ModelDefaultsConfig {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "model defaults")?;
        let side = |name: &str| match o.get(name) {
            None => Ok(Some(ProviderModeDefaults::default())),
            value => json::record(value),
        };
        Ok(ModelDefaultsConfig { claude: side("claude")?, codex: side("codex")? })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop("claude", self.claude.as_ref());
        out.prop("codex", self.codex.as_ref());
        out.end_object();
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct ProviderModeDefaults {
    pub mode_defaults: Option<StringMap>,
    /// Never normalized: a null element stays and is written back as null.
    pub registered_models: Option<Vec<Option<RegisteredModelEntry>>>,
}

impl Default for ProviderModeDefaults {
    fn default() -> Self {
        ProviderModeDefaults { mode_defaults: Some(IndexMap::new()), registered_models: Some(vec![]) }
    }
}

impl Wire for ProviderModeDefaults {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "provider defaults")?;
        Ok(ProviderModeDefaults {
            mode_defaults: match o.get("modeDefaults") {
                None => Some(IndexMap::new()),
                value => json::string_map(value)?,
            },
            registered_models: match o.get("registeredModels") {
                None => Some(vec![]),
                value => json::list(value, RegisteredModelEntry::read)?,
            },
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_string_map("modeDefaults", self.mode_defaults.as_ref());
        if let Some(list) = &self.registered_models {
            out.name("registeredModels");
            write_nullable_list(out, list);
        }
        out.end_object();
    }
}

/// A list whose null elements are written as null.
pub fn write_nullable_list<T: Wire>(out: &mut JsonOut, list: &[Option<T>]) {
    out.begin_array();
    for item in list {
        match item {
            Some(item) => item.write(out),
            None => out.null(),
        }
    }
    out.end_array();
}

pub fn write_nullable_strings(out: &mut JsonOut, list: &[Option<String>]) {
    out.begin_array();
    for item in list {
        match item {
            Some(item) => out.str(item),
            None => out.null(),
        }
    }
    out.end_array();
}

#[derive(Debug, Clone, PartialEq)]
pub struct RegisteredModelEntry {
    pub name: Option<String>,
    pub supports_effort: bool,
    pub supported_effort_levels: Option<Vec<Option<String>>>,
}

impl Wire for RegisteredModelEntry {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a registered model")?;
        Ok(RegisteredModelEntry {
            name: json::opt_string(o.get("name"))?,
            supports_effort: json::boolean(o.get("supportsEffort"), false)?,
            supported_effort_levels: json::string_list(o.get("supportedEffortLevels"))?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("name", self.name.as_deref());
        out.prop_bool("supportsEffort", self.supports_effort);
        if let Some(levels) = &self.supported_effort_levels {
            out.name("supportedEffortLevels");
            write_nullable_strings(out, levels);
        }
        out.end_object();
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct PhaseModelsSnapshot {
    pub claude_main: Option<String>,
    pub claude_opus_alias: Option<String>,
    pub claude_sonnet_alias: Option<String>,
    pub claude_haiku_alias: Option<String>,
    pub claude_subagent_default: Option<String>,
    pub codex_review_model: Option<String>,
    pub codex_subagent_default: Option<String>,
    pub codex_plan_mode_reasoning_effort: Option<String>,
    pub claude_main_effort: Option<String>,
    pub codex_main_effort: Option<String>,
    pub codex_subagent_effort: Option<String>,
}

impl Wire for PhaseModelsSnapshot {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "phase models")?;
        let model = |name: &str| json::string(o.get(name), some("default"));
        Ok(PhaseModelsSnapshot {
            claude_main: model("claudeMain")?,
            claude_opus_alias: model("claudeOpusAlias")?,
            claude_sonnet_alias: model("claudeSonnetAlias")?,
            claude_haiku_alias: model("claudeHaikuAlias")?,
            claude_subagent_default: model("claudeSubagentDefault")?,
            codex_review_model: model("codexReviewModel")?,
            codex_subagent_default: model("codexSubagentDefault")?,
            codex_plan_mode_reasoning_effort: model("codexPlanModeReasoningEffort")?,
            claude_main_effort: json::opt_string(o.get("claudeMainEffort"))?,
            codex_main_effort: json::opt_string(o.get("codexMainEffort"))?,
            codex_subagent_effort: json::opt_string(o.get("codexSubagentEffort"))?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("claudeMain", self.claude_main.as_deref());
        out.prop_str("claudeOpusAlias", self.claude_opus_alias.as_deref());
        out.prop_str("claudeSonnetAlias", self.claude_sonnet_alias.as_deref());
        out.prop_str("claudeHaikuAlias", self.claude_haiku_alias.as_deref());
        out.prop_str("claudeSubagentDefault", self.claude_subagent_default.as_deref());
        out.prop_str("codexReviewModel", self.codex_review_model.as_deref());
        out.prop_str("codexSubagentDefault", self.codex_subagent_default.as_deref());
        out.prop_str("codexPlanModeReasoningEffort", self.codex_plan_mode_reasoning_effort.as_deref());
        out.prop_str("claudeMainEffort", self.claude_main_effort.as_deref());
        out.prop_str("codexMainEffort", self.codex_main_effort.as_deref());
        out.prop_str("codexSubagentEffort", self.codex_subagent_effort.as_deref());
        out.end_object();
    }
}

/// C# `MobileRemoteSettings(Enabled = false, RelayURL = "", AllowLegacyPhones = true, DirectEnabled = true, DirectPort = 43139)`.
#[derive(Debug, Clone, PartialEq)]
pub struct MobileRemoteSettings {
    pub enabled: bool,
    pub relay_url: Option<String>,
    pub allow_legacy_phones: bool,
    pub direct_enabled: bool,
    pub direct_port: i32,
}

/// C# `MobileDirect.DefaultPort`.
pub const MOBILE_DIRECT_PORT: i32 = 43139;

impl Default for MobileRemoteSettings {
    fn default() -> Self {
        MobileRemoteSettings {
            enabled: false,
            relay_url: Some(String::new()),
            allow_legacy_phones: true,
            direct_enabled: true,
            direct_port: MOBILE_DIRECT_PORT,
        }
    }
}

impl Wire for MobileRemoteSettings {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "mobile remote settings")?;
        Ok(MobileRemoteSettings {
            enabled: json::boolean(o.get("enabled"), false)?,
            relay_url: json::string(o.get("relayURL"), some(""))?,
            allow_legacy_phones: json::boolean(o.get("allowLegacyPhones"), true)?,
            direct_enabled: json::boolean(o.get("directEnabled"), true)?,
            direct_port: json::int32(o.get("directPort"), MOBILE_DIRECT_PORT)?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_bool("enabled", self.enabled);
        out.prop_str("relayURL", self.relay_url.as_deref());
        out.prop_bool("allowLegacyPhones", self.allow_legacy_phones);
        out.prop_bool("directEnabled", self.direct_enabled);
        out.prop_int("directPort", self.direct_port as i64);
        out.end_object();
    }
}

// ── RunSession ──────────────────────────────────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, PartialEq)]
pub struct RunSession {
    pub id: Option<String>,
    pub workspace_id: Option<String>,
    pub title: Option<String>,
    pub kind: Option<String>,
    pub status: Option<String>,
    pub model: Option<String>,
    pub provider: Option<String>,
    pub settings: Option<RunSettings>,
    pub logs: Option<Vec<LogEntry>>,
    pub resume_id: Option<String>,
    pub created_at: Option<String>,
    pub draft: Option<String>,
    pub run_timing: Option<AgentRunTiming>,
    pub session_usage: Option<SessionUsage>,
    /// `[JsonIgnore]`: the activity a running pane shows now, never saved.
    pub current_activity: Option<AgentActivity>,
    pub agent_view_mode: Option<String>,
    pub mighty_style: Option<String>,
    pub mighty_style_hash: Option<String>,
    pub mighty_style_since: Option<String>,
    pub graph_runs: Option<Vec<MightyGraphRun>>,
    pub graph_result_size: Option<GraphBlockSize>,
    pub graph_plan_size: Option<GraphBlockSize>,
    pub graph_block_sizes: Option<IndexMap<String, Option<GraphBlockSize>>>,
    /// Null when read without the field (a pane saved before the 실행 split); 1 on a pane made new (`GraphBlockPreferences.SplitLayout`).
    pub graph_sizes_layout: Option<i32>,
    pub graph_view_mode: Option<String>,
    /// Written only when true (the setter turns false into null).
    pub flow_log_collapsed: Option<bool>,
    pub workspace_profile_key: Option<String>,
    pub owner_session_id: Option<String>,
    pub title_mode: Option<String>,
    pub plan_history: Option<Vec<PlanRecord>>,
    pub todo_progress: Option<TodoProgress>,
    pub background_work: Option<BackgroundWork>,
    pub parent_session_id: Option<String>,
    pub working_folder: Option<String>,
    pub ask_target: Option<String>,
    /// `shellRuns` on the wire.
    pub shell_block_runs: Option<Vec<ShellBlockRun>>,
}

impl Default for RunSession {
    /// `new RunSession()`: a new id, the time now, the current graph size layout.
    fn default() -> Self {
        RunSession {
            id: Some(new_id()),
            workspace_id: Some(String::new()),
            title: Some("Claude".into()),
            kind: Some("claude".into()),
            status: Some("idle".into()),
            model: Some("default".into()),
            provider: Some("claude".into()),
            settings: Some(RunSettings::default()),
            logs: Some(vec![]),
            resume_id: None,
            created_at: Some(time::now_o()),
            draft: Some(String::new()),
            run_timing: None,
            session_usage: None,
            current_activity: None,
            agent_view_mode: None,
            mighty_style: None,
            mighty_style_hash: None,
            mighty_style_since: None,
            graph_runs: None,
            graph_result_size: None,
            graph_plan_size: None,
            graph_block_sizes: None,
            graph_sizes_layout: Some(crate::graph::SPLIT_LAYOUT),
            graph_view_mode: None,
            flow_log_collapsed: None,
            workspace_profile_key: None,
            owner_session_id: None,
            title_mode: None,
            plan_history: None,
            todo_progress: None,
            background_work: None,
            parent_session_id: None,
            working_folder: None,
            ask_target: None,
            shell_block_runs: None,
        }
    }
}

/// `MightyTimeline.NormalizeMode`.
pub fn graph_view_mode(value: Option<&str>) -> Option<String> {
    value.filter(|v| matches!(*v, "diagram" | "timeline" | "structure")).map(str::to_string)
}

/// `LenientListConverter<T>.Read`: an array whose elements that fail to read (or are null) are left out; anything else is null.
fn lenient_list<T: Wire>(value: Option<&Value>) -> Option<Vec<T>> {
    match value {
        Some(Value::Array(items)) => Some(items.iter().filter(|item| !item.is_null()).filter_map(|item| T::read(item).ok()).collect()),
        _ => None,
    }
}

/// `LenientJsonConverter<T>.Read`: the record, or null when it fails to read.
fn lenient_record<T: Wire>(value: Option<&Value>) -> Option<T> {
    value.filter(|v| !v.is_null()).and_then(|v| T::read(v).ok())
}

impl Wire for RunSession {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a pane")?;
        Ok(RunSession {
            id: json::string(o.get("id"), || Some(new_id()))?,
            workspace_id: json::string(o.get("workspaceId"), some(""))?,
            title: json::string(o.get("title"), some("Claude"))?,
            kind: json::string(o.get("kind"), some("claude"))?,
            status: json::string(o.get("status"), some("idle"))?,
            model: json::string(o.get("model"), some("default"))?,
            provider: json::string(o.get("provider"), some("claude"))?,
            settings: match o.get("settings") {
                None => Some(RunSettings::default()),
                value => json::record(value)?,
            },
            logs: match o.get("logs") {
                None => Some(vec![]),
                // A null entry is never read: Normalize leaves it out first.
                value => json::skipping_list(value)?,
            },
            resume_id: json::opt_string(o.get("resumeId"))?,
            created_at: json::string(o.get("createdAt"), || Some(time::now_o()))?,
            draft: json::string(o.get("draft"), some(""))?,
            run_timing: match o.get("runTiming") {
                None | Some(Value::Null) => None,
                Some(v) => AgentRunTiming::from_json(v),
            },
            session_usage: match o.get("sessionUsage") {
                None | Some(Value::Null) => None,
                Some(v) => SessionUsage::from_json(v),
            },
            current_activity: None,
            agent_view_mode: o
                .get("agentViewMode")
                .and_then(Value::as_str)
                .filter(|v| matches!(*v, "default" | "mighty"))
                .map(str::to_string),
            mighty_style: json::opt_string(o.get("mightyStyle"))?,
            mighty_style_hash: json::opt_string(o.get("mightyStyleHash"))?,
            mighty_style_since: json::opt_string(o.get("mightyStyleSince"))?,
            graph_runs: json::strict_list(o.get("graphRuns"), "graph run")?,
            graph_result_size: json::record(o.get("graphResultSize"))?,
            graph_plan_size: json::record(o.get("graphPlanSize"))?,
            graph_block_sizes: json::dictionary(o.get("graphBlockSizes"), GraphBlockSize::read)?,
            // IJsonOnDeserializing clears it first: absent reads as null; GraphSizesLayoutConverter takes a whole number only.
            graph_sizes_layout: o.get("graphSizesLayout").and_then(json::as_integer).and_then(|n| i32::try_from(n).ok()),
            graph_view_mode: graph_view_mode(o.get("graphViewMode").and_then(Value::as_str)),
            flow_log_collapsed: (lenient_bool(o.get("flowLogCollapsed")) == Some(true)).then_some(true),
            workspace_profile_key: json::opt_string(o.get("workspaceProfileKey"))?,
            owner_session_id: json::opt_string(o.get("ownerSessionId"))?,
            title_mode: json::opt_string(o.get("titleMode"))?,
            plan_history: lenient_list(o.get("planHistory")),
            todo_progress: lenient_record(o.get("todoProgress")),
            background_work: lenient_record(o.get("backgroundWork")),
            parent_session_id: lenient_string(o.get("parentSessionId")),
            working_folder: lenient_string(o.get("workingFolder")),
            ask_target: lenient_string(o.get("askTarget")),
            shell_block_runs: lenient_list(o.get("shellRuns")),
        })
    }

    fn write(&self, out: &mut JsonOut) {
        self.write_with(out, true, true);
    }
}

impl RunSession {
    /// Written with or without its two lists (the delta's fields leave `logs` and `graphRuns` out), and with or without the graph
    /// agents' output files (`WebDelta.PageRuns`).
    pub fn write_with(&self, out: &mut JsonOut, lists: bool, output_files: bool) {
        out.begin_object();
        out.prop_str("id", self.id.as_deref());
        out.prop_str("workspaceId", self.workspace_id.as_deref());
        out.prop_str("title", self.title.as_deref());
        out.prop_str("kind", self.kind.as_deref());
        out.prop_str("status", self.status.as_deref());
        out.prop_str("model", self.model.as_deref());
        out.prop_str("provider", self.provider.as_deref());
        out.prop("settings", self.settings.as_ref());
        if lists {
            out.prop_list("logs", self.logs.as_ref());
        }
        out.prop_str("resumeId", self.resume_id.as_deref());
        out.prop_str("createdAt", self.created_at.as_deref());
        out.prop_str("draft", self.draft.as_deref());
        out.prop("runTiming", self.run_timing.as_ref());
        out.prop("sessionUsage", self.session_usage.as_ref());
        if let Some(mode) = &self.agent_view_mode {
            // AgentViewModeConverter writes anything but its two words as null.
            out.name("agentViewMode");
            if matches!(mode.as_str(), "default" | "mighty") { out.str(mode) } else { out.null() }
        }
        out.prop_str("mightyStyle", self.mighty_style.as_deref());
        out.prop_str("mightyStyleHash", self.mighty_style_hash.as_deref());
        out.prop_str("mightyStyleSince", self.mighty_style_since.as_deref());
        if lists && let Some(runs) = &self.graph_runs {
            out.name("graphRuns");
            out.begin_array();
            for run in runs {
                run.write_with(out, output_files);
            }
            out.end_array();
        }
        out.prop("graphResultSize", self.graph_result_size.as_ref());
        out.prop("graphPlanSize", self.graph_plan_size.as_ref());
        if let Some(sizes) = &self.graph_block_sizes {
            out.name("graphBlockSizes");
            out.begin_object();
            for (key, size) in sizes {
                out.name(key);
                match size {
                    Some(size) => size.write(out),
                    None => out.null(),
                }
            }
            out.end_object();
        }
        out.prop_opt_int("graphSizesLayout", self.graph_sizes_layout.map(i64::from));
        if let Some(mode) = &self.graph_view_mode {
            out.name("graphViewMode");
            match graph_view_mode(Some(mode)) {
                Some(mode) => out.str(&mode),
                None => out.null(),
            }
        }
        out.prop_opt_bool("flowLogCollapsed", self.flow_log_collapsed);
        out.prop_str("workspaceProfileKey", self.workspace_profile_key.as_deref());
        out.prop_str("ownerSessionId", self.owner_session_id.as_deref());
        out.prop_str("titleMode", self.title_mode.as_deref());
        out.prop_list("planHistory", self.plan_history.as_ref());
        out.prop("todoProgress", self.todo_progress.as_ref());
        out.prop("backgroundWork", self.background_work.as_ref());
        out.prop_str("parentSessionId", self.parent_session_id.as_deref());
        out.prop_str("workingFolder", self.working_folder.as_deref());
        out.prop_str("askTarget", self.ask_target.as_deref());
        out.prop_list("shellRuns", self.shell_block_runs.as_ref());
        out.end_object();
    }

    pub fn id(&self) -> &str {
        self.id.as_deref().unwrap_or("")
    }
    pub fn workspace_id(&self) -> &str {
        self.workspace_id.as_deref().unwrap_or("")
    }
    pub fn title(&self) -> &str {
        self.title.as_deref().unwrap_or("")
    }
    pub fn kind(&self) -> &str {
        self.kind.as_deref().unwrap_or("")
    }
    pub fn status(&self) -> &str {
        self.status.as_deref().unwrap_or("")
    }
    pub fn model(&self) -> &str {
        self.model.as_deref().unwrap_or("")
    }
    pub fn provider(&self) -> &str {
        self.provider.as_deref().unwrap_or("")
    }
    pub fn created_at(&self) -> Option<&str> {
        self.created_at.as_deref()
    }
    pub fn logs(&self) -> &[LogEntry] {
        self.logs.as_deref().unwrap_or(&[])
    }
    pub fn graph_runs(&self) -> &[MightyGraphRun] {
        self.graph_runs.as_deref().unwrap_or(&[])
    }
}

/// A pane's run settings (C# `RunSettings` and its `RunSettingsJsonConverter`, which reads the exact names and always writes
/// `maxTurns`/`maxBudgetUsd`, null when unset, for legacy v1 hosts).
#[derive(Debug, Clone, PartialEq)]
pub struct RunSettings {
    pub effort: String,
    pub permission_mode: String,
    pub max_turns: Option<i32>,
    pub max_budget_usd: Option<f64>,
    pub fast_mode: bool,
    pub web_search: String,
    pub network_access: bool,
    pub advisor: Option<String>,
}

impl Default for RunSettings {
    fn default() -> Self {
        RunSettings {
            effort: "default".into(),
            permission_mode: "manual".into(),
            max_turns: None,
            max_budget_usd: None,
            fast_mode: false,
            web_search: "default".into(),
            network_access: false,
            advisor: None,
        }
    }
}

impl Wire for RunSettings {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "run settings")?;
        let text = |name: &str, default: &str| match o.exact(name) {
            None => Ok(default.to_string()),
            Some(Value::String(s)) => Ok(s.clone()),
            Some(_) => fail(format!("run settings {name} is not a string")),
        };
        let flag = |name: &str| match o.exact(name) {
            None => Ok(false),
            Some(Value::Bool(b)) => Ok(*b),
            Some(_) => fail(format!("run settings {name} is not a bool")),
        };
        Ok(RunSettings {
            effort: text("effort", "default")?,
            permission_mode: text("permissionMode", "manual")?,
            max_turns: match o.exact("maxTurns") {
                None | Some(Value::Null) => None,
                Some(v) => Some(json::as_integer(v).and_then(|n| i32::try_from(n).ok()).ok_or_else(|| json::ReadError("maxTurns".into()))?),
            },
            max_budget_usd: match o.exact("maxBudgetUsd") {
                None | Some(Value::Null) => None,
                Some(v) => {
                    Some(v.as_f64().filter(|n| v.is_number() && n.is_finite()).ok_or_else(|| json::ReadError("maxBudgetUsd".into()))?)
                }
            },
            fast_mode: flag("fastMode")?,
            web_search: text("webSearch", "default")?,
            network_access: flag("networkAccess")?,
            advisor: o.text("advisor").filter(|a| !a.is_empty()).map(str::to_string),
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("effort", Some(&self.effort));
        out.prop_str("permissionMode", Some(&self.permission_mode));
        out.name("maxTurns");
        match self.max_turns {
            Some(turns) => out.int(turns as i64),
            None => out.null(),
        }
        out.name("maxBudgetUsd");
        match self.max_budget_usd {
            Some(budget) => out.float(budget),
            None => out.null(),
        }
        if self.fast_mode {
            out.prop_bool("fastMode", true);
        }
        if self.web_search != "default" {
            out.prop_str("webSearch", Some(&self.web_search));
        }
        if self.network_access {
            out.prop_bool("networkAccess", true);
        }
        out.prop_str("advisor", self.advisor.as_deref());
        out.end_object();
    }
}

// ── LogEntry and what it holds ──────────────────────────────────────────────────────────────────────────────────────────

/// One transcript entry (C# `LogEntry(Id, Kind, Text, Timestamp, Provider?, Activity?, Images?)`).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct LogEntry {
    pub id: Option<String>,
    pub kind: Option<String>,
    pub text: Option<String>,
    pub timestamp: Option<String>,
    pub provider: Option<String>,
    pub activity: Option<AgentActivity>,
    /// A null picture is kept until `AgentImageSupport.Normalized` counts and drops it.
    pub images: Option<Vec<Option<AgentImageRef>>>,
}

/// The entry kinds a transcript keeps (C# `LogEntry.Stored`).
pub const STORED_KINDS: [&str; 6] = ["user", "assistant", "system", "output", "error", "image"];

impl Wire for LogEntry {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a log entry")?;
        Ok(LogEntry {
            id: json::opt_string(o.get("id"))?,
            kind: json::opt_string(o.get("kind"))?,
            text: json::opt_string(o.get("text"))?,
            timestamp: json::opt_string(o.get("timestamp"))?,
            provider: json::opt_string(o.get("provider"))?,
            activity: match o.get("activity") {
                None | Some(Value::Null) => None,
                Some(v) => AgentActivity::from_json(v),
            },
            images: json::list(o.get("images"), AgentImageRef::read)?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("id", self.id.as_deref());
        out.prop_str("kind", self.kind.as_deref());
        out.prop_str("text", self.text.as_deref());
        out.prop_str("timestamp", self.timestamp.as_deref());
        out.prop_str("provider", self.provider.as_deref());
        out.prop("activity", self.activity.as_ref());
        if let Some(images) = &self.images {
            out.name("images");
            write_nullable_list(out, images);
        }
        out.end_object();
    }
}

impl LogEntry {
    pub fn new(id: String, kind: &str, text: String, timestamp: String, provider: Option<String>) -> Self {
        LogEntry {
            id: Some(id),
            kind: Some(kind.into()),
            text: Some(text),
            timestamp: Some(timestamp),
            provider,
            activity: None,
            images: None,
        }
    }
    pub fn id(&self) -> &str {
        self.id.as_deref().unwrap_or("")
    }
    pub fn kind(&self) -> &str {
        self.kind.as_deref().unwrap_or("")
    }
    pub fn text(&self) -> &str {
        self.text.as_deref().unwrap_or("")
    }
}

/// C# `AgentActivity(Id, Provider, Kind, State, Summary, ToolName?, Output?, DurationMs?)` with its `AgentActivityConverter`, which
/// normalizes what it reads (`ActivitySupport.Normalize`) and loads anything invalid as null.
#[derive(Debug, Clone, PartialEq)]
pub struct AgentActivity {
    pub id: String,
    pub provider: String,
    pub kind: String,
    pub state: String,
    pub summary: String,
    pub tool_name: Option<String>,
    pub output: Option<String>,
    pub duration_ms: Option<f64>,
}

impl AgentActivity {
    pub fn from_json(v: &Value) -> Option<AgentActivity> {
        crate::normalize::activity(
            &AgentActivity {
                id: json::text(v, "id").unwrap_or("").into(),
                provider: json::text(v, "provider").unwrap_or("").into(),
                kind: json::text(v, "kind").unwrap_or("").into(),
                state: json::text(v, "state").unwrap_or("").into(),
                summary: json::text(v, "summary").unwrap_or("").into(),
                tool_name: json::text(v, "toolName").map(str::to_string),
                output: json::text(v, "output").map(str::to_string),
                duration_ms: json::number(v, "durationMs"),
            },
            false,
        )
    }
}

impl Wire for AgentActivity {
    fn read(value: &Value) -> R<Self> {
        AgentActivity::from_json(value).ok_or_else(|| json::ReadError("an invalid activity".into()))
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("id", Some(&self.id));
        out.prop_str("provider", Some(&self.provider));
        out.prop_str("kind", Some(&self.kind));
        out.prop_str("state", Some(&self.state));
        out.prop_str("summary", Some(&self.summary));
        out.prop_str("toolName", self.tool_name.as_deref());
        out.prop_str("output", self.output.as_deref());
        out.prop_opt_float("durationMs", self.duration_ms);
        out.end_object();
    }
}

/// C# `AgentImageRef(Hash, MediaType, Width, Height, Bytes, Source, Path?)`.
#[derive(Debug, Clone, PartialEq)]
pub struct AgentImageRef {
    pub hash: Option<String>,
    pub media_type: Option<String>,
    pub width: i32,
    pub height: i32,
    pub bytes: i32,
    pub source: Option<String>,
    pub path: Option<String>,
}

impl Wire for AgentImageRef {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a picture")?;
        Ok(AgentImageRef {
            hash: json::opt_string(o.get("hash"))?,
            media_type: json::opt_string(o.get("mediaType"))?,
            width: json::int32(o.get("width"), 0)?,
            height: json::int32(o.get("height"), 0)?,
            bytes: json::int32(o.get("bytes"), 0)?,
            source: json::opt_string(o.get("source"))?,
            path: json::opt_string(o.get("path"))?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("hash", self.hash.as_deref());
        out.prop_str("mediaType", self.media_type.as_deref());
        out.prop_int("width", self.width as i64);
        out.prop_int("height", self.height as i64);
        out.prop_int("bytes", self.bytes as i64);
        out.prop_str("source", self.source.as_deref());
        out.prop_str("path", self.path.as_deref());
        out.end_object();
    }
}

/// C# `AgentRunTiming(StartedAt, LastObservedAt, FinishedAt?, IsApproximate)` with its converter: times in `"O"`, an invalid
/// record read as null.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct AgentRunTiming {
    pub started_at: DateTimeOffset,
    pub last_observed_at: DateTimeOffset,
    pub finished_at: Option<DateTimeOffset>,
    pub is_approximate: bool,
}

impl AgentRunTiming {
    pub fn is_valid(&self) -> bool {
        self.last_observed_at >= self.started_at && self.finished_at.is_none_or(|f| f >= self.last_observed_at)
    }

    /// `Interrupt()`: a run the quit cut off ends where it was last seen, approximately.
    pub fn interrupt(self) -> Self {
        if self.finished_at.is_some() {
            return self;
        }
        AgentRunTiming { finished_at: Some(self.last_observed_at), is_approximate: true, ..self }
    }

    /// `Observe(at)`.
    pub fn observe(self, at: Option<DateTimeOffset>) -> Self {
        if self.finished_at.is_some() {
            return self;
        }
        let now = at.unwrap_or_else(DateTimeOffset::now);
        // new[] { StartedAt, LastObservedAt, now }.Max(): the first of the greatest.
        let mut latest = self.started_at;
        for candidate in [self.last_observed_at, now] {
            if candidate > latest {
                latest = candidate;
            }
        }
        AgentRunTiming { last_observed_at: latest, ..self }
    }

    pub fn from_json(v: &Value) -> Option<AgentRunTiming> {
        let start = time::parse(json::text(v, "startedAt"))?;
        let end = time::parse(json::text(v, "finishedAt"));
        let observed = time::parse(json::text(v, "lastObservedAt")).or(end).unwrap_or(start);
        let present = |name: &str| v.as_object().and_then(|o| o.get(name)).is_some_and(|x| !x.is_null());
        if present("finishedAt") && end.is_none() || present("lastObservedAt") && time::parse(json::text(v, "lastObservedAt")).is_none() {
            return None;
        }
        let value = AgentRunTiming {
            started_at: start,
            last_observed_at: observed,
            finished_at: end,
            is_approximate: json::flag(v, "isApproximate"),
        };
        value.is_valid().then_some(value)
    }

    /// `Infer(logs)`: from the latest request to the latest answer after it, approximately.
    pub fn infer(logs: &[LogEntry]) -> Option<AgentRunTiming> {
        let index = logs.iter().rposition(|e| e.kind() == "user")?;
        let start = time::parse(logs[index].timestamp.as_deref())?;
        let mut latest: Option<DateTimeOffset> = None;
        for entry in &logs[index + 1..] {
            if !(matches!(entry.kind(), "assistant" | "output" | "error") || entry.activity.is_some()) {
                continue;
            }
            if let Some(date) = time::parse(entry.timestamp.as_deref()).filter(|d| *d > start)
                && latest.is_none_or(|l| date > l)
            {
                latest = Some(date);
            }
        }
        latest.map(|end| AgentRunTiming { started_at: start, last_observed_at: end, finished_at: Some(end), is_approximate: true })
    }
}

impl Wire for AgentRunTiming {
    fn read(value: &Value) -> R<Self> {
        AgentRunTiming::from_json(value).ok_or_else(|| json::ReadError("an invalid run timing".into()))
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("startedAt", Some(&self.started_at.to_o()));
        out.prop_str("lastObservedAt", Some(&self.last_observed_at.to_o()));
        out.prop_str("finishedAt", self.finished_at.map(|f| f.to_o()).as_deref());
        out.prop_bool("isApproximate", self.is_approximate);
        out.end_object();
    }
}

/// C# `SessionRateLimit(Kind, PercentUsed?, ResetsAt?)`.
#[derive(Debug, Clone, PartialEq)]
pub struct SessionRateLimit {
    pub kind: String,
    pub percent_used: Option<f64>,
    pub resets_at: Option<String>,
}

impl Wire for SessionRateLimit {
    fn read(value: &Value) -> R<Self> {
        Ok(SessionRateLimit {
            kind: json::text(value, "kind").unwrap_or("").into(),
            percent_used: json::number(value, "percentUsed"),
            resets_at: json::text(value, "resetsAt").map(str::to_string),
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("kind", Some(&self.kind));
        out.prop_opt_float("percentUsed", self.percent_used);
        out.prop_str("resetsAt", self.resets_at.as_deref());
        out.end_object();
    }
}

/// C# `SessionUsage` with its `SessionUsageConverter`, which normalizes what it reads (`SessionUsageSupport.Normalize`).
#[derive(Debug, Clone, PartialEq)]
pub struct SessionUsage {
    pub provider: String,
    pub source: String,
    pub token_scope: String,
    pub model: Option<String>,
    pub selected_model: Option<String>,
    pub provider_session_id: Option<String>,
    pub input_tokens: Option<i64>,
    pub output_tokens: Option<i64>,
    pub cache_read_tokens: Option<i64>,
    pub cache_write_tokens: Option<i64>,
    pub reasoning_tokens: Option<i64>,
    pub total_tokens: Option<i64>,
    pub context_used_tokens: Option<i64>,
    pub context_window_tokens: Option<i64>,
    pub cost_usd: Option<f64>,
    pub cost_scope: Option<String>,
    pub rate_limits: Option<Vec<SessionRateLimit>>,
    pub rate_limits_updated_at: Option<String>,
    pub updated_at: String,
}

impl SessionUsage {
    pub fn from_json(v: &Value) -> Option<SessionUsage> {
        let limits = match v.as_object().and_then(|o| o.get("rateLimits")) {
            Some(Value::Array(items)) => {
                Some(items.iter().take(16).map(|r| SessionRateLimit::read(r).unwrap_or_else(|_| unreachable!())).collect())
            }
            _ => None,
        };
        let text = |name: &str| json::text(v, name).map(str::to_string);
        crate::normalize::session_usage(&SessionUsage {
            provider: text("provider").unwrap_or_default(),
            source: text("source").unwrap_or_default(),
            token_scope: text("tokenScope").unwrap_or_default(),
            model: text("model"),
            selected_model: text("selectedModel"),
            provider_session_id: text("providerSessionId"),
            input_tokens: json::integer(v, "inputTokens"),
            output_tokens: json::integer(v, "outputTokens"),
            cache_read_tokens: json::integer(v, "cacheReadTokens"),
            cache_write_tokens: json::integer(v, "cacheWriteTokens"),
            reasoning_tokens: json::integer(v, "reasoningTokens"),
            total_tokens: json::integer(v, "totalTokens"),
            context_used_tokens: json::integer(v, "contextUsedTokens"),
            context_window_tokens: json::integer(v, "contextWindowTokens"),
            cost_usd: json::number(v, "costUSD"),
            cost_scope: text("costScope"),
            rate_limits: limits,
            rate_limits_updated_at: text("rateLimitsUpdatedAt"),
            updated_at: text("updatedAt").unwrap_or_default(),
        })
    }
}

impl Wire for SessionUsage {
    fn read(value: &Value) -> R<Self> {
        SessionUsage::from_json(value).ok_or_else(|| json::ReadError("an invalid usage".into()))
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("provider", Some(&self.provider));
        out.prop_str("source", Some(&self.source));
        out.prop_str("tokenScope", Some(&self.token_scope));
        out.prop_str("model", self.model.as_deref());
        out.prop_str("selectedModel", self.selected_model.as_deref());
        out.prop_str("providerSessionId", self.provider_session_id.as_deref());
        out.prop_opt_int("inputTokens", self.input_tokens);
        out.prop_opt_int("outputTokens", self.output_tokens);
        out.prop_opt_int("cacheReadTokens", self.cache_read_tokens);
        out.prop_opt_int("cacheWriteTokens", self.cache_write_tokens);
        out.prop_opt_int("reasoningTokens", self.reasoning_tokens);
        out.prop_opt_int("totalTokens", self.total_tokens);
        out.prop_opt_int("contextUsedTokens", self.context_used_tokens);
        out.prop_opt_int("contextWindowTokens", self.context_window_tokens);
        out.prop_opt_float("costUSD", self.cost_usd);
        out.prop_str("costScope", self.cost_scope.as_deref());
        out.prop_list("rateLimits", self.rate_limits.as_ref());
        out.prop_str("rateLimitsUpdatedAt", self.rate_limits_updated_at.as_deref());
        out.prop_str("updatedAt", Some(&self.updated_at));
        out.end_object();
    }
}

// ── Plan, checklist, background work, shell block runs ──────────────────────────────────────────────────────────────────

/// C# `PlanRecord(Id, RunId, Plan, ReceivedAt, DecidedAt, Outcome, Feedback?, PlanTruncated?, GraphRunId?, LaunchOverride?)`.
#[derive(Debug, Clone, PartialEq)]
pub struct PlanRecord {
    pub id: Option<String>,
    pub run_id: Option<String>,
    pub plan: Option<String>,
    pub received_at: Option<String>,
    pub decided_at: Option<String>,
    pub outcome: Option<String>,
    pub feedback: Option<String>,
    pub plan_truncated: Option<bool>,
    pub graph_run_id: Option<String>,
    pub launch_override: Option<String>,
}

impl Wire for PlanRecord {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a plan record")?;
        Ok(PlanRecord {
            id: json::opt_string(o.get("id"))?,
            run_id: json::opt_string(o.get("runId"))?,
            plan: json::opt_string(o.get("plan"))?,
            received_at: json::opt_string(o.get("receivedAt"))?,
            decided_at: json::opt_string(o.get("decidedAt"))?,
            outcome: json::opt_string(o.get("outcome"))?,
            feedback: json::opt_string(o.get("feedback"))?,
            plan_truncated: json::opt_boolean(o.get("planTruncated"))?,
            graph_run_id: json::opt_string(o.get("graphRunId"))?,
            launch_override: json::opt_string(o.get("launchOverride"))?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("id", self.id.as_deref());
        out.prop_str("runId", self.run_id.as_deref());
        out.prop_str("plan", self.plan.as_deref());
        out.prop_str("receivedAt", self.received_at.as_deref());
        out.prop_str("decidedAt", self.decided_at.as_deref());
        out.prop_str("outcome", self.outcome.as_deref());
        out.prop_str("feedback", self.feedback.as_deref());
        out.prop_opt_bool("planTruncated", self.plan_truncated);
        out.prop_str("graphRunId", self.graph_run_id.as_deref());
        out.prop_str("launchOverride", self.launch_override.as_deref());
        out.end_object();
    }
}

/// C# `TodoItem(Content, Status, ActiveForm?, Id?)`.
#[derive(Debug, Clone, PartialEq)]
pub struct TodoItem {
    pub content: Option<String>,
    pub status: Option<String>,
    pub active_form: Option<String>,
    pub id: Option<String>,
}

impl Wire for TodoItem {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a checklist item")?;
        Ok(TodoItem {
            content: json::opt_string(o.get("content"))?,
            status: json::opt_string(o.get("status"))?,
            active_form: json::opt_string(o.get("activeForm"))?,
            id: json::opt_string(o.get("id"))?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("content", self.content.as_deref());
        out.prop_str("status", self.status.as_deref());
        out.prop_str("activeForm", self.active_form.as_deref());
        out.prop_str("id", self.id.as_deref());
        out.end_object();
    }
}

/// C# `TodoProgress(Items, RunId?)`.
#[derive(Debug, Clone, PartialEq)]
pub struct TodoProgress {
    pub items: Option<Vec<TodoItem>>,
    pub run_id: Option<String>,
}

impl Wire for TodoProgress {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a checklist")?;
        Ok(TodoProgress { items: json::skipping_list(o.get("items"))?, run_id: json::opt_string(o.get("runId"))? })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_list("items", self.items.as_ref());
        out.prop_str("runId", self.run_id.as_deref());
        out.end_object();
    }
}

/// C# `BackgroundTask(Id, Kind, Description, StartedAt, Status = "running", ToolUseId?, Summary?, EndedAt?)`.
#[derive(Debug, Clone, PartialEq)]
pub struct BackgroundTask {
    pub id: Option<String>,
    pub kind: Option<String>,
    pub description: Option<String>,
    pub started_at: Option<String>,
    pub status: Option<String>,
    pub tool_use_id: Option<String>,
    pub summary: Option<String>,
    pub ended_at: Option<String>,
}

impl Wire for BackgroundTask {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a background task")?;
        Ok(BackgroundTask {
            id: json::opt_string(o.get("id"))?,
            kind: json::opt_string(o.get("kind"))?,
            description: json::opt_string(o.get("description"))?,
            started_at: json::opt_string(o.get("startedAt"))?,
            status: json::string(o.get("status"), some("running"))?,
            tool_use_id: json::opt_string(o.get("toolUseId"))?,
            summary: json::opt_string(o.get("summary"))?,
            ended_at: json::opt_string(o.get("endedAt"))?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("id", self.id.as_deref());
        out.prop_str("kind", self.kind.as_deref());
        out.prop_str("description", self.description.as_deref());
        out.prop_str("startedAt", self.started_at.as_deref());
        out.prop_str("status", self.status.as_deref());
        out.prop_str("toolUseId", self.tool_use_id.as_deref());
        out.prop_str("summary", self.summary.as_deref());
        out.prop_str("endedAt", self.ended_at.as_deref());
        out.end_object();
    }
}

/// C# `BackgroundWork(Tasks, TurnEnded = false)`.
#[derive(Debug, Clone, PartialEq)]
pub struct BackgroundWork {
    pub tasks: Option<Vec<BackgroundTask>>,
    pub turn_ended: bool,
}

impl Wire for BackgroundWork {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "background work")?;
        Ok(BackgroundWork { tasks: json::skipping_list(o.get("tasks"))?, turn_ended: json::boolean(o.get("turnEnded"), false)? })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_list("tasks", self.tasks.as_ref());
        out.prop_bool("turnEnded", self.turn_ended);
        out.end_object();
    }
}

/// C# `ShellBlockRun(Key, Command, State, ExitCode?, Output, StartedAt, EndedAt?)`.
#[derive(Debug, Clone, PartialEq)]
pub struct ShellBlockRun {
    pub key: Option<String>,
    pub command: Option<String>,
    pub state: Option<String>,
    pub exit_code: Option<i32>,
    pub output: Option<String>,
    pub started_at: Option<String>,
    pub ended_at: Option<String>,
}

impl Wire for ShellBlockRun {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a shell block run")?;
        Ok(ShellBlockRun {
            key: json::opt_string(o.get("key"))?,
            command: json::opt_string(o.get("command"))?,
            state: json::opt_string(o.get("state"))?,
            exit_code: json::opt_int32(o.get("exitCode"))?,
            output: json::opt_string(o.get("output"))?,
            started_at: json::opt_string(o.get("startedAt"))?,
            ended_at: json::opt_string(o.get("endedAt"))?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("key", self.key.as_deref());
        out.prop_str("command", self.command.as_deref());
        out.prop_str("state", self.state.as_deref());
        out.prop_opt_int("exitCode", self.exit_code.map(i64::from));
        out.prop_str("output", self.output.as_deref());
        out.prop_str("startedAt", self.started_at.as_deref());
        out.prop_str("endedAt", self.ended_at.as_deref());
        out.end_object();
    }
}

// ── Layout ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// A workspace layout node (C# `PaneLayoutNode`): a tab group (`kind: "tabs"`) or a binary split.
#[derive(Debug, Clone, PartialEq)]
pub struct PaneLayoutNode {
    pub id: Option<String>,
    pub kind: Option<String>,
    pub session_ids: Option<Vec<Option<String>>>,
    pub selected_session_id: Option<String>,
    pub axis: Option<String>,
    pub ratio: f64,
    pub children: Option<Vec<Option<PaneLayoutNode>>>,
}

impl Default for PaneLayoutNode {
    /// `new PaneLayoutNode()`: a tab group with a new id.
    fn default() -> Self {
        PaneLayoutNode {
            id: Some(new_id()),
            kind: Some("tabs".into()),
            session_ids: Some(vec![]),
            selected_session_id: None,
            axis: None,
            ratio: 0.5,
            children: Some(vec![]),
        }
    }
}

impl Wire for PaneLayoutNode {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a layout node")?;
        Ok(PaneLayoutNode {
            id: json::string(o.get("id"), || Some(new_id()))?,
            kind: json::string(o.get("kind"), some("tabs"))?,
            session_ids: match o.get("sessionIds") {
                None => Some(vec![]),
                value => json::string_list(value)?,
            },
            selected_session_id: json::opt_string(o.get("selectedSessionId"))?,
            axis: json::opt_string(o.get("axis"))?,
            ratio: json::float64(o.get("ratio"), 0.5)?,
            children: match o.get("children") {
                None => Some(vec![]),
                value => json::list(value, PaneLayoutNode::read)?,
            },
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("id", self.id.as_deref());
        out.prop_str("kind", self.kind.as_deref());
        if let Some(ids) = &self.session_ids {
            out.name("sessionIds");
            write_nullable_strings(out, ids);
        }
        out.prop_str("selectedSessionId", self.selected_session_id.as_deref());
        out.prop_str("axis", self.axis.as_deref());
        out.prop_float("ratio", self.ratio);
        if let Some(children) = &self.children {
            out.name("children");
            write_nullable_list(out, children);
        }
        out.end_object();
    }
}

impl PaneLayoutNode {
    pub fn id(&self) -> &str {
        self.id.as_deref().unwrap_or("")
    }
    pub fn kind(&self) -> &str {
        self.kind.as_deref().unwrap_or("")
    }
    /// The panes of a tab group (a null id left out).
    pub fn session_ids(&self) -> impl Iterator<Item = &str> {
        self.session_ids.iter().flatten().flatten().map(String::as_str)
    }
    pub fn children(&self) -> impl Iterator<Item = &PaneLayoutNode> {
        self.children.iter().flatten().flatten()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_session_round_trips_in_csharp_order() {
        let text = r#"{"id":"s1","workspaceId":"w1","title":"T","kind":"claude","status":"idle","model":"default","provider":"claude","settings":{"effort":"default","permissionMode":"manual","maxTurns":null,"maxBudgetUsd":null},"logs":[],"createdAt":"2026-01-01T00:00:00Z","draft":"","graphSizesLayout":1}"#;
        let value: Value = serde_json::from_str(text).unwrap();
        let session = RunSession::read(&value).unwrap();
        assert_eq!(session.to_json(), text);
    }

    #[test]
    fn absent_fields_take_the_csharp_defaults_and_nulls_stay_null() {
        let value: Value = serde_json::from_str(r#"{"version":1,"workspaces":[],"sessions":[],"theme":null,"Layout":"tabs"}"#).unwrap();
        let state = AppSnapshot::read(&value).unwrap();
        assert_eq!(state.theme, None);
        assert_eq!(state.layout.as_deref(), Some("tabs"));
        assert_eq!(state.sidebar_width, 252.0);
        assert!(state.status_line_enabled);
        assert!(AppSnapshot::read(&serde_json::from_str::<Value>(r#"{"sidebarCollapsed":"yes"}"#).unwrap()).is_err());
        let lenient =
            AppSnapshot::read(&serde_json::from_str::<Value>(r#"{"autoLoginCLIs":"yes","referenceBubbleWidth":"x"}"#).unwrap()).unwrap();
        assert_eq!(lenient.auto_login_clis, None);
        assert_eq!(lenient.reference_bubble_width, None);
    }
}
