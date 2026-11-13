//! The saved state, `workspace-state.json` version 1 (C# `AppSnapshot`, `Workspace`, `RunSession`, `RunSettings`, `LogEntry`,
//! `PaneLayoutNode` in MightyCode.Core/Models.cs and PaneLayout.cs).
//!
//! JSON naming is C# `Wire.Json`'s: camelCase, a null left out. Every field this host does not read yet is kept in the record's
//! `extra` map (`#[serde(flatten)]`), and a known field is an `Option` that stays absent when the file left it out, so a load and
//! a save give back what was read. The C# defaults a field takes when absent are the accessors' (`theme()`, `layout()`, ...), and
//! the page gets them filled in by `normalize::for_page`. A field C# reads leniently is a [`Lenient`]; any other field of the wrong
//! type fails the load, as it does in C#.

use std::collections::BTreeMap;

use serde::{Deserialize, Serialize, Serializer};
use serde_json::{Map, Value};

use crate::wire::Lenient;

/// The fields of a record this host does not model, kept as they were read.
pub type Extra = Map<String, Value>;

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppSnapshot {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub version: Option<i64>,
    #[serde(default)]
    pub workspaces: Vec<Workspace>,
    #[serde(default)]
    pub sessions: Vec<RunSession>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub active_workspace_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub active_session_id: Option<String>,
    /// Null (absent) opens the active workspace only; `[]` means all closed (C# `WorkspaceDisclosureConverter`: anything but an array
    /// of strings reads as null).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub expanded_workspace_ids: Option<Lenient<Vec<String>>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub layout: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub pane_layouts: Option<BTreeMap<String, PaneLayoutNode>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub pane_layout_modes: Option<BTreeMap<String, String>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub pane_layout_active_session_ids: Option<BTreeMap<String, String>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub theme: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sidebar_width: Option<f64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sidebar_collapsed: Option<bool>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub status_line_enabled: Option<bool>,
    /// "system", "ko", "en", "zh" or "ja".
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub language_preference: Option<String>,
    #[serde(flatten)]
    pub extra: Extra,
}

impl AppSnapshot {
    pub fn theme(&self) -> &str {
        self.theme.as_deref().unwrap_or("dark")
    }
    pub fn layout(&self) -> &str {
        self.layout.as_deref().unwrap_or("grid")
    }
    pub fn language_preference(&self) -> &str {
        self.language_preference.as_deref().unwrap_or("system")
    }
    pub fn workspace(&self, id: &str) -> Option<&Workspace> {
        self.workspaces.iter().find(|w| w.id() == id)
    }
    pub fn session(&self, id: &str) -> Option<&RunSession> {
        self.sessions.iter().find(|s| s.id() == id)
    }
    /// The ids of a workspace's panes, in the snapshot's order.
    pub fn pane_ids(&self, workspace_id: &str) -> Vec<String> {
        self.sessions.iter().filter(|s| s.workspace_id() == workspace_id).map(|s| s.id().to_string()).collect()
    }
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Workspace {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub created_at: Option<String>,
    /// Set when the folder is on another computer (version 2 only; `store::keeps_workspace` drops any other shape at load, so a
    /// loaded workspace's is always valid; a raw decode keeps whatever was saved).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub remote: Option<Lenient<RemoteWorkspaceReference>>,
    #[serde(flatten)]
    pub extra: Extra,
}

impl Workspace {
    /// The other computer's reference, when the workspace is a remote one.
    pub fn remote(&self) -> Option<&RemoteWorkspaceReference> {
        self.remote.as_ref().and_then(Lenient::get)
    }
    pub fn id(&self) -> &str {
        self.id.as_deref().unwrap_or("")
    }
    pub fn name(&self) -> &str {
        self.name.as_deref().unwrap_or("Workspace")
    }
    pub fn path(&self) -> &str {
        self.path.as_deref().unwrap_or("")
    }
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RemoteWorkspaceReference {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub version: Option<Value>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub connection_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub workspace_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub host_name: Option<String>,
    #[serde(flatten)]
    pub extra: Extra,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RunSession {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub workspace_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub title: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub kind: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub status: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub model: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub provider: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub settings: Option<RunSettings>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub logs: Option<Vec<LogEntry>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub resume_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub created_at: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub draft: Option<String>,
    /// "auto" (or absent) while an agent pane's title follows its latest request, "fixed" once renamed.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub title_mode: Option<String>,
    /// A delegated child's parent pane (C# reads it leniently).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub parent_session_id: Option<Lenient<String>>,
    #[serde(flatten)]
    pub extra: Extra,
}

impl RunSession {
    pub fn id(&self) -> &str {
        self.id.as_deref().unwrap_or("")
    }
    pub fn workspace_id(&self) -> &str {
        self.workspace_id.as_deref().unwrap_or("")
    }
    pub fn title(&self) -> &str {
        self.title.as_deref().unwrap_or("Claude")
    }
    pub fn kind(&self) -> &str {
        self.kind.as_deref().unwrap_or("claude")
    }
    pub fn status(&self) -> &str {
        self.status.as_deref().unwrap_or("idle")
    }
    pub fn model(&self) -> &str {
        self.model.as_deref().unwrap_or("default")
    }
    pub fn provider(&self) -> &str {
        self.provider.as_deref().unwrap_or("claude")
    }
    pub fn created_at(&self) -> Option<&str> {
        self.created_at.as_deref()
    }
    pub fn logs(&self) -> &[LogEntry] {
        self.logs.as_deref().unwrap_or(&[])
    }
}

/// A pane's run settings (C# `RunSettings` and `RunSettingsJsonConverter`). `maxTurns` and `maxBudgetUsd` are always written, null
/// when unset, as the C# converter writes them for legacy v1 hosts.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RunSettings {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub effort: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub permission_mode: Option<String>,
    #[serde(default, serialize_with = "always")]
    pub max_turns: Option<i64>,
    #[serde(default, serialize_with = "always")]
    pub max_budget_usd: Option<f64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub fast_mode: Option<bool>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub web_search: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub network_access: Option<bool>,
    /// Anything but a name loads as no choice in C#.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub advisor: Option<Lenient<String>>,
    #[serde(flatten)]
    pub extra: Extra,
}

fn always<T: Serialize, S: Serializer>(value: &Option<T>, serializer: S) -> Result<S::Ok, S::Error> {
    value.serialize(serializer)
}

/// One transcript entry (C# `LogEntry`). The activity and the pictures are kept as read.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogEntry {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub kind: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub text: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub timestamp: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub provider: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub activity: Option<Value>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub images: Option<Value>,
    #[serde(flatten)]
    pub extra: Extra,
}

impl LogEntry {
    pub fn kind(&self) -> &str {
        self.kind.as_deref().unwrap_or("")
    }
    pub fn text(&self) -> &str {
        self.text.as_deref().unwrap_or("")
    }
}

/// A workspace layout node as saved: a tab group (`kind: "tabs"`) or a binary split. `layout::Node` is the same with the C#
/// defaults filled in, which the layout rules work on.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PaneLayoutNode {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub kind: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub session_ids: Option<Vec<String>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub selected_session_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub axis: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ratio: Option<f64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub children: Option<Vec<PaneLayoutNode>>,
    #[serde(flatten)]
    pub extra: Extra,
}
