//! The state as the page gets it: the structural part of C# `StateStore.Normalize(restoring: true)` with every field the page reads
//! filled with its C# default. What the C# normalize also does and this does not yet: the text budget, the transcript, graph-run,
//! plan, todo and background-work limits, the provider settings rules (`ProviderCatalog.NormalizeSettings`), the phase models, the
//! status-line trust and mobile settings checks. Those fields pass through as read.

use std::collections::{BTreeMap, HashSet};

use serde_json::{Value, json};

use crate::layout::{self, Node};
use crate::model::{AppSnapshot, LogEntry, RunSession, RunSettings, Workspace};
use crate::wire::{self, Lenient, PROVIDERS};

/// The entry kinds a transcript keeps (C# `LogEntry.Stored`).
pub const STORED_KINDS: [&str; 6] = ["user", "assistant", "system", "output", "error", "image"];
/// C# `TranscriptRetention.MaximumEntries`.
pub const MAXIMUM_ENTRIES: usize = 400;
/// C# `RenameSupport.MaximumTextElements` and `PaneTitle.MaximumTextElements`.
pub const TITLE_ELEMENTS: usize = 120;
pub const SHORT_TITLE_ELEMENTS: usize = 40;
/// C# `DesignMetrics.Layout`.
pub const SIDEBAR_MIN: f64 = 210.0;
pub const SIDEBAR_DEFAULT: f64 = 252.0;
pub const SIDEBAR_MAX: f64 = 360.0;
/// C# `MobileDirect.DefaultPort`.
pub const MOBILE_DIRECT_PORT: u16 = 43139;
const LAYOUTS: [&str; 5] = ["grid", "columns", "focus", "tabs", "custom"];
const SETTINGS_PANES: [&str; 9] = ["general", "models", "styles", "tools", "cli", "mobile", "remote", "companion", "about"];
/// What C# `Path.IsPathFullyQualified` accepts on the OS this runs on.
pub fn fully_qualified(path: &str) -> bool {
    if cfg!(windows) {
        let b = path.as_bytes();
        (b.len() >= 3 && b[0].is_ascii_alphabetic() && b[1] == b':' && (b[2] == b'\\' || b[2] == b'/'))
            || path.starts_with("\\\\")
            || path.starts_with("//")
    } else {
        path.starts_with('/')
    }
}

/// A host's folder: absolute on the host's own OS, whatever this OS calls absolute (C# `StateStore.RemotePath`).
pub fn remote_path(path: &str) -> bool {
    let b = path.as_bytes();
    !path.is_empty()
        && path.len() <= 4096
        && !path.contains('\0')
        && (path.starts_with('/')
            || path.starts_with("\\\\")
            || (b.len() >= 3 && b[0].is_ascii_alphabetic() && b[1] == b':' && (b[2] == b'\\' || b[2] == b'/')))
}

/// C# `StateStore.ValidWorkspace`.
pub fn valid_workspace(w: &Workspace) -> bool {
    let path = w.path();
    if !wire::identifier(w.id()) || path.is_empty() || path.len() > 4096 || path.contains('\0') {
        return false;
    }
    match &w.remote {
        None => fully_qualified(path),
        Some(Lenient::Valid(r)) => {
            r.version.as_ref().and_then(Value::as_f64) == Some(2.0)
                && r.connection_id.as_deref().is_some_and(wire::identifier)
                && r.workspace_id.as_deref().is_some_and(wire::identifier)
                && !wire::clean(r.host_name.as_deref().unwrap_or(""), 120).trim().is_empty()
                && remote_path(path)
        }
        Some(Lenient::Invalid(_)) => false,
    }
}

/// C# `RenameSupport.ClampTitle`: control characters removed, at most 120 text elements.
pub fn clamp_title(value: &str) -> String {
    let clean = wire::clean(value, 32768);
    wire::text_elements(&clean, TITLE_ELEMENTS).0.to_string()
}

/// C# `PaneTitle.Shortened`: whitespace collapsed, at most 40 text elements and "…".
pub fn shortened(raw: &str) -> Option<String> {
    let collapsed = raw.split_whitespace().collect::<Vec<_>>().join(" ");
    if collapsed.is_empty() {
        return None;
    }
    let (head, cut) = wire::text_elements(&collapsed, SHORT_TITLE_ELEMENTS);
    Some(if cut { format!("{head}…") } else { collapsed })
}

const ATTACHMENT_MARKER: &str = "\u{CCA8}\u{BD80}: ";
const ASK_SKILL: &str = "oh-my-claudecode:ask";
const ASK_TARGETS: [&str; 5] = ["codex", "antigravity", "gemini", "grok", "cursor"];

/// C# `PaneTitle.FollowsRequests`.
pub fn follows_requests(s: &RunSession) -> bool {
    s.kind() == "claude" && matches!(s.title_mode.as_deref(), None | Some("auto"))
}

/// C# `PaneTitle.Tooltip`: the latest request's words, without its attachment list.
pub fn tooltip(s: &RunSession) -> Option<String> {
    s.logs().iter().rev().filter(|e| e.kind() == "user").find_map(|e| {
        let text = e.text();
        let text =
            if text.starts_with(ATTACHMENT_MARKER) { "" } else { text.split(&format!("\n\n{ATTACHMENT_MARKER}")).next().unwrap_or("") };
        let trimmed = text.trim();
        (!trimmed.is_empty()).then(|| trimmed.to_string())
    })
}

/// C# `AskTargets.Question`: a request asked of another CLI, without its `/oh-my-claudecode:ask <target> ` prefix.
pub fn ask_question(text: &str) -> &str {
    let trimmed = text.trim();
    for target in ASK_TARGETS {
        if let Some(rest) = trimmed.strip_prefix(&format!("/{ASK_SKILL} {target} ")) {
            return rest;
        }
    }
    text
}

/// C# `ProviderCatalog.Name`.
pub fn provider_name(provider: &str) -> &'static str {
    match provider {
        "claude" => "Claude Code",
        "codex" => "Codex CLI",
        "pi" => "pi",
        "opencode" => "opencode CLI",
        "hermes" => "Hermes CLI",
        _ => "Antigravity CLI",
    }
}

/// C# `PaneTitle.Restored`: a pane whose title follows its requests is titled by its latest one again.
fn restored_title(s: &RunSession) -> Option<String> {
    if !follows_requests(s) {
        return None;
    }
    let fallback =
        if s.resume_id.is_some() && !s.title().is_empty() { s.title().to_string() } else { provider_name(s.provider()).to_string() };
    Some(tooltip(s).and_then(|text| shortened(ask_question(&text))).unwrap_or(fallback))
}

fn normalize_log(entry: &LogEntry) -> Option<LogEntry> {
    if !entry.id.as_deref().is_some_and(wire::identifier) || !STORED_KINDS.contains(&entry.kind()) {
        return None;
    }
    let limit = if entry.kind() == "assistant" { 1024 * 1024 } else { 32768 };
    let text = wire::clean(entry.text(), limit);
    if text.is_empty() {
        return None;
    }
    let provider = entry.provider.clone().filter(|p| PROVIDERS.contains(&p.as_str()));
    let images = if entry.kind() == "image" { entry.images.clone() } else { None };
    Some(LogEntry { text: Some(text), provider, images, ..entry.clone() })
}

fn default_settings() -> RunSettings {
    RunSettings { effort: Some("default".into()), permission_mode: Some("manual".into()), ..Default::default() }
}

fn normalize_session(s: &RunSession) -> RunSession {
    let mut out = s.clone();
    out.title = Some(clamp_title(s.title()));
    out.kind = Some(s.kind().to_string());
    out.created_at = Some(s.created_at().unwrap_or("").to_string());
    if s.kind() == "browser" {
        out.status = Some("idle".into());
        out.logs = Some(vec![]);
        out.draft = Some(String::new());
        out.resume_id = None;
        out.parent_session_id = None;
        out.model = Some(s.model().to_string());
        out.provider = Some(s.provider().to_string());
        out.settings = Some(s.settings.clone().unwrap_or_else(default_settings));
        return out;
    }
    let provider = if PROVIDERS.contains(&s.provider()) { s.provider() } else { "claude" };
    out.provider = Some(provider.into());
    out.model = Some(if wire::model(s.model()) { s.model() } else { "default" }.into());
    out.settings = Some(s.settings.clone().unwrap_or_else(default_settings));
    let logs: Vec<LogEntry> = s.logs().iter().filter_map(normalize_log).collect();
    let skip = logs.len().saturating_sub(MAXIMUM_ENTRIES);
    out.logs = Some(logs.into_iter().skip(skip).collect());
    out.resume_id = s.resume_id.clone().filter(|id| wire::session_identifier(id));
    out.status = Some(
        match s.status() {
            "running" if s.kind() == "shell" => "idle",
            "running" => "stopped",
            status @ ("idle" | "completed" | "error" | "stopped") => status,
            _ => "idle",
        }
        .into(),
    );
    out.draft = Some(wire::clean(s.draft.as_deref().unwrap_or(""), 100_000));
    out.title_mode = s.title_mode.clone().filter(|m| m == "auto" || m == "fixed");
    out.parent_session_id = match &s.parent_session_id {
        Some(Lenient::Valid(parent)) if s.kind() == "claude" && wire::identifier(parent) && parent != s.id() => {
            Some(Lenient::Valid(parent.clone()))
        }
        _ => None,
    };
    if let Some(title) = restored_title(&out) {
        out.title = Some(title);
    }
    out
}

/// The snapshot as the page gets it (see the module comment). `None` version reads as 1, as a new C# `AppSnapshot` does.
pub fn for_page(value: &AppSnapshot) -> AppSnapshot {
    let mut snapshot = if value.version.unwrap_or(1) == 1 { value.clone() } else { AppSnapshot::default() };
    snapshot.version = Some(1);

    let mut seen = HashSet::new();
    let workspaces: Vec<Workspace> = snapshot
        .workspaces
        .iter()
        .filter(|w| valid_workspace(w) && seen.insert(w.id().to_string()))
        .take(64)
        .map(|w| {
            let mut w = w.clone();
            w.name = Some(wire::clean(w.name(), 120));
            w.path = Some(w.path().to_string());
            if let Some(Lenient::Valid(remote)) = w.remote.as_mut() {
                remote.host_name = Some(wire::clean(remote.host_name.as_deref().unwrap_or(""), 120));
            }
            w
        })
        .collect();
    let ids: HashSet<String> = workspaces.iter().map(|w| w.id().to_string()).collect();

    let mut seen = HashSet::new();
    let sessions: Vec<RunSession> = snapshot
        .sessions
        .iter()
        .filter(|s| {
            wire::identifier(s.id())
                && ids.contains(s.workspace_id())
                && matches!(s.kind(), "claude" | "shell" | "browser")
                && seen.insert(s.id().to_string())
        })
        .take(layout::MAXIMUM_SESSIONS)
        .map(normalize_session)
        .collect();

    let workspace_id =
        snapshot.active_workspace_id.clone().filter(|id| ids.contains(id)).or_else(|| workspaces.first().map(|w| w.id().to_string()));
    let in_active = |s: &&RunSession| Some(s.workspace_id()) == workspace_id.as_deref();
    let active_session_id = sessions
        .iter()
        .filter(in_active)
        .find(|s| Some(s.id()) == snapshot.active_session_id.as_deref())
        .or_else(|| sessions.iter().find(in_active))
        .map(|s| s.id().to_string());

    snapshot.expanded_workspace_ids = match &snapshot.expanded_workspace_ids {
        Some(Lenient::Valid(list)) => {
            let mut kept: Vec<String> = list.iter().filter(|id| ids.contains(*id)).cloned().collect();
            kept.sort();
            kept.dedup();
            Some(Lenient::Valid(kept))
        }
        _ => None,
    };

    let pane_ids = |workspace: &str| -> Vec<String> {
        sessions.iter().filter(|s| s.workspace_id() == workspace).map(|s| s.id().to_string()).collect()
    };
    let saved_selections = snapshot.pane_layout_active_session_ids.clone().unwrap_or_default();
    let layouts: Option<BTreeMap<String, Node>> = snapshot.pane_layouts.as_ref().map(|saved| {
        saved
            .iter()
            .filter(|(key, _)| ids.contains(*key))
            .filter_map(|(key, node)| {
                let active = if Some(key.as_str()) == workspace_id.as_deref() {
                    active_session_id.clone()
                } else {
                    saved_selections.get(key).cloned()
                };
                layout::normalize(Some(&Node::from_raw(node)), &pane_ids(key), active.as_deref()).map(|n| (key.clone(), n))
            })
            .collect()
    });

    let mut modes = BTreeMap::new();
    let mut selections = BTreeMap::new();
    let saved_modes = snapshot.pane_layout_modes.clone();
    let saved_layout = snapshot.layout().to_string();
    for workspace in &workspaces {
        let id = workspace.id();
        let root = layouts.as_ref().and_then(|l| l.get(id));
        let saved_mode = saved_modes.as_ref().and_then(|m| m.get(id)).filter(|m| LAYOUTS.contains(&m.as_str()));
        let mode = match saved_mode {
            Some(mode) => mode.clone(),
            None if saved_modes.is_none() && saved_layout == "focus" && Some(id) == workspace_id.as_deref() => "focus".into(),
            None => match root {
                Some(root) if root.kind == "tabs" => "tabs".into(),
                Some(_) => "custom".into(),
                None if matches!(saved_layout.as_str(), "grid" | "columns" | "tabs") => saved_layout.clone(),
                None => "grid".into(),
            },
        };
        modes.insert(id.to_string(), mode);
        let panes = pane_ids(id);
        let preferred = if Some(id) == workspace_id.as_deref() { active_session_id.clone() } else { saved_selections.get(id).cloned() };
        let selected = preferred
            .filter(|p| panes.contains(p))
            .or_else(|| {
                root.and_then(|r| {
                    layout::groups(r).first().and_then(|g| g.selected_session_id.clone().or_else(|| g.session_ids.first().cloned()))
                })
            })
            .or_else(|| panes.first().cloned());
        if let Some(selected) = selected {
            selections.insert(id.to_string(), selected);
        }
    }

    snapshot.workspaces = workspaces;
    snapshot.sessions = sessions;
    snapshot.active_workspace_id = workspace_id;
    snapshot.active_session_id = active_session_id;
    snapshot.pane_layouts = layouts.map(|l| l.into_iter().map(|(k, n)| (k, n.to_raw())).collect());
    snapshot.pane_layout_modes = Some(modes);
    snapshot.pane_layout_active_session_ids = Some(selections);
    snapshot.layout = Some(if LAYOUTS.contains(&snapshot.layout()) { snapshot.layout() } else { "grid" }.into());
    snapshot.theme = Some(if snapshot.theme() == "light" { "light" } else { "dark" }.into());
    snapshot.sidebar_width = Some(match snapshot.sidebar_width {
        Some(w) if w.is_finite() => w.clamp(SIDEBAR_MIN, SIDEBAR_MAX),
        Some(_) => SIDEBAR_DEFAULT,
        None => SIDEBAR_DEFAULT,
    });
    snapshot.sidebar_collapsed = Some(snapshot.sidebar_collapsed.unwrap_or(false));
    snapshot.status_line_enabled = Some(snapshot.status_line_enabled.unwrap_or(true));
    let preference = snapshot.language_preference().to_string();
    snapshot.language_preference = Some(if crate::locale::LANGUAGES.contains(&preference.as_str()) { preference } else { "system".into() });

    let extra = &mut snapshot.extra;
    let pane = extra.get("settingsPane").and_then(Value::as_str).filter(|p| SETTINGS_PANES.contains(p)).unwrap_or("general").to_string();
    extra.insert("settingsPane".into(), Value::String(pane));
    for (key, default) in [
        ("completionNotificationsEnabled", json!(true)),
        ("claudeDirectUsageLookupEnabled", json!(false)),
        ("appUpdateAutoCheck", json!(true)),
        ("browserEngineEnabled", json!(false)),
        (
            "mobileRemote",
            json!({"enabled": false, "relayURL": "", "allowLegacyPhones": true, "directEnabled": true, "directPort": MOBILE_DIRECT_PORT}),
        ),
    ] {
        if extra.get(key).is_none_or(Value::is_null) {
            extra.insert(key.into(), default);
        }
    }
    snapshot
}

#[cfg(test)]
mod tests {
    use super::*;

    fn state(json: Value) -> AppSnapshot {
        serde_json::from_value(json).unwrap()
    }

    fn root() -> &'static str {
        if cfg!(windows) { "C:\\work\\a" } else { "/work/a" }
    }

    #[test]
    fn restore_stops_running_panes_and_fills_defaults() {
        let s = state(json!({
            "version": 1,
            "workspaces": [{"id": "w1", "name": "A", "path": root()}],
            "sessions": [
                {"id": "s1", "workspaceId": "w1", "status": "running", "titleMode": "fixed", "title": "One"},
                {"id": "s2", "workspaceId": "w1", "kind": "shell", "status": "running", "title": "Two"},
                {"id": "s3", "workspaceId": "gone", "title": "Orphan"}
            ]
        }));
        let page = for_page(&s);
        assert_eq!(page.sessions.len(), 2);
        assert_eq!(page.sessions[0].status(), "stopped");
        assert_eq!(page.sessions[1].status(), "idle");
        assert_eq!(page.active_workspace_id.as_deref(), Some("w1"));
        assert_eq!(page.active_session_id.as_deref(), Some("s1"));
        assert_eq!(page.pane_layout_modes.as_ref().unwrap()["w1"], "grid");
        assert_eq!(page.extra["mobileRemote"]["directPort"], json!(43139));
        assert_eq!(page.sidebar_width, Some(252.0));
    }

    #[test]
    fn an_automatic_title_follows_the_latest_request() {
        let s = state(json!({
            "version": 1,
            "workspaces": [{"id": "w1", "path": root()}],
            "sessions": [{"id": "s1", "workspaceId": "w1", "title": "Claude", "logs": [
                {"id": "l1", "kind": "user", "text": "first", "timestamp": "2026-10-01T00:00:00Z"},
                {"id": "l2", "kind": "user", "text": "  fix   the\nbuild  ", "timestamp": "2026-10-01T00:00:00Z"}
            ]}]
        }));
        assert_eq!(for_page(&s).sessions[0].title(), "fix the build");
    }

    #[test]
    fn shortened_cuts_at_forty_elements() {
        let long = "a".repeat(50);
        assert_eq!(shortened(&long).unwrap(), format!("{}…", "a".repeat(40)));
        assert_eq!(shortened("   "), None);
    }
}
