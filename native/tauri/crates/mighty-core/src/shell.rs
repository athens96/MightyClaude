//! The frame's model the page draws (`{kind:"shell", shell}`): C# `WebShellModel` (MightyCode.WinUI/MainWindow.WebShell.Shell.cs)
//! read from the same rules — `StatusGlyph`, `ProviderMark`, `ProviderCatalog`, `PaneTitle`, `WorkDashboard`, `WorkspaceDisclosure`,
//! `PaneLayout` and the window's `EffectiveLayout`/`LayoutMode`.
//!
//! What the skeleton has no source for yet is left out or at its resting value: no pane waits on a tool permission or question
//! (attention 0), no delegation file is read (every pane is a top-level sidebar row), no git info (the work-map button says the
//! folder is no repository, as C# does before its first git read), no runtime check, no usage, no app update, no dashboard,
//! no companion. Model names in the dashboard meta are the raw ids (C# names them from the provider's model catalogue).

use std::collections::{HashMap, HashSet};

use chrono::{DateTime, Utc};
use serde::Serialize;

use crate::layout::{self, Node};
use crate::locale::Locale;
use crate::model::{AppSnapshot, RunSession, Workspace};
use crate::wire;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Tone {
    Run,
    Wait,
    Done,
    Err,
    Stop,
    Idle,
}

impl Tone {
    /// C# `StatusGlyph.Tone`.
    pub fn of(status: &str) -> Tone {
        match status {
            "running" => Tone::Run,
            "waiting" => Tone::Wait,
            "completed" => Tone::Done,
            "error" | "failed" => Tone::Err,
            "stopped" | "cancelled" | "interrupted" => Tone::Stop,
            _ => Tone::Idle,
        }
    }
    pub fn name(self) -> &'static str {
        match self {
            Tone::Run => "run",
            Tone::Wait => "wait",
            Tone::Done => "done",
            Tone::Err => "err",
            Tone::Stop => "stop",
            Tone::Idle => "idle",
        }
    }
    /// C# `StatusGlyph.WordKey`.
    pub fn word_key(self) -> &'static str {
        match self {
            Tone::Run => "session.state.running",
            Tone::Wait => "phone.dashboard.stat.waiting",
            Tone::Done => "session.state.completed",
            Tone::Err => "session.state.error",
            Tone::Stop => "session.state.stopped",
            Tone::Idle => "session.state.idle",
        }
    }
}

/// C# `StatusGlyph.DisplayStatus`.
pub fn display_status(status: &str, pending: usize) -> &str {
    if pending > 0 { "waiting" } else { status }
}

/// C# `WorkDashboard.IsCounted`: the panes the counts take in.
pub fn is_counted(kind: &str) -> bool {
    !matches!(kind, "files" | "workmap" | "agent-terminal" | "agent-browser")
}

/// C# `ProviderCatalog.IsBeta`.
pub fn is_beta(provider: &str) -> bool {
    matches!(provider, "codex" | "gemini" | "pi" | "opencode" | "hermes")
}

/// C# `ProviderMark.Label`.
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

/// C# `ProviderMark.SidebarProvider`: an agent pane's provider, when it is one the app knows.
pub fn sidebar_provider(s: &RunSession) -> Option<&str> {
    (s.kind() == "claude" && wire::PROVIDERS.contains(&s.provider())).then(|| s.provider())
}

#[derive(Debug, Clone, Default, Serialize, PartialEq)]
pub struct Counts {
    pub questions: usize,
    pub permissions: usize,
    pub errors: usize,
    pub running: usize,
    pub done: usize,
    pub stopped: usize,
    pub idle: usize,
}

/// C# `WorkDashboard.WorkspaceBadges` with no pane waiting on the user.
pub fn badges<'a>(sessions: impl Iterator<Item = &'a RunSession>) -> Counts {
    let mut counts = Counts::default();
    for pane in sessions.filter(|s| is_counted(s.kind())) {
        match Tone::of(display_status(pane.status(), 0)) {
            Tone::Err => counts.errors += 1,
            Tone::Run => counts.running += 1,
            Tone::Done => counts.done += 1,
            Tone::Stop => counts.stopped += 1,
            Tone::Idle => counts.idle += 1,
            Tone::Wait => {}
        }
    }
    counts
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct PaneInfo {
    pub id: String,
    pub workspace_id: String,
    pub title: String,
    pub help: String,
    pub kind: String,
    pub provider: String,
    pub beta: bool,
    pub tone: &'static str,
    pub word: String,
    pub settled: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub agent: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub agent_label: Option<String>,
    pub meta: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub wait: Option<String>,
    pub tab_mark: bool,
    pub waiting: usize,
    pub dash_meta: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub activity: Option<String>,
    pub activity_error: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub dash_wait: Option<String>,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct SidebarItem {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub id: String,
    pub depth: usize,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct SidebarWorkspace {
    pub id: String,
    pub name: String,
    pub path: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub remote_host: Option<String>,
    pub local: bool,
    pub counts: Counts,
    pub expanded: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub items: Option<Vec<SidebarItem>>,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Header {
    pub id: String,
    pub name: String,
    pub path: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub remote_host: Option<String>,
    pub work_map_enabled: bool,
    pub work_map_help: String,
    pub files_enabled: bool,
    pub files_help: String,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct LayoutModel {
    pub workspace_id: String,
    pub mode: String,
    pub root: Node,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Status {
    pub machine: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub remote: Option<bool>,
    pub panes: String,
    pub running: String,
    pub update_help: String,
    pub setup: bool,
    pub chips_title: String,
    pub chips_tooltip: String,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ShellModel {
    pub version: String,
    pub dialog_open: bool,
    pub panes: Vec<PaneInfo>,
    pub entry: Counts,
    pub workspaces: Vec<SidebarWorkspace>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub header: Option<Header>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub layout: Option<LayoutModel>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
    pub status: Status,
    pub setup_needed: bool,
}

/// C# `WorkspaceDisclosure.Expanded`: the saved list, or the active workspace when none was saved.
pub fn expanded(state: &AppSnapshot) -> HashSet<String> {
    let listed: Vec<String> = match state.expanded_workspace_ids.as_ref().and_then(|l| l.get()) {
        Some(list) => list.clone(),
        None => state.active_workspace_id.iter().cloned().collect(),
    };
    listed.into_iter().filter(|id| state.workspace(id).is_some()).collect()
}

/// C# `WorkspaceDisclosure.Toggle`: the list sorted ordinally, as C# keeps it.
pub fn toggle_disclosure(state: &AppSnapshot, id: &str) -> Option<Vec<String>> {
    state.workspace(id)?;
    let mut ids = expanded(state);
    if !ids.remove(id) {
        ids.insert(id.to_string());
    }
    let mut list: Vec<String> = ids.into_iter().collect();
    list.sort();
    Some(list)
}

/// C# `MainWindow.LayoutMode`.
pub fn layout_mode(state: &AppSnapshot, workspace: &str) -> String {
    state.pane_layout_modes.as_ref().and_then(|m| m.get(workspace)).cloned().unwrap_or_else(|| state.layout().to_string())
}

/// C# `MainWindow.EffectiveLayout`: the saved tree, else the one made before (kept so its ids stay put), else the mode's preset;
/// normalized to the workspace's panes, the active one selected. `defaults` is the window's `layoutDefaults`.
pub fn effective_layout(state: &AppSnapshot, workspace_id: &str, defaults: &mut HashMap<String, Node>) -> Option<Node> {
    let ids = state.pane_ids(workspace_id);
    let active_here = state.active_workspace_id.as_deref() == Some(workspace_id);
    let saved = state.pane_layouts.as_ref().and_then(|l| l.get(workspace_id)).map(Node::from_raw);
    let node = saved.or_else(|| defaults.get(workspace_id).cloned()).or_else(|| {
        let preferred = if active_here {
            state.active_session_id.clone()
        } else {
            state.pane_layout_active_session_ids.as_ref().and_then(|m| m.get(workspace_id)).cloned()
        };
        layout::preset(&ids, &layout_mode(state, workspace_id), preferred.as_deref())
    });
    let node = layout::normalize(node.as_ref(), &ids, if active_here { state.active_session_id.as_deref() } else { None });
    if let Some(node) = &node {
        defaults.insert(workspace_id.to_string(), node.clone());
    }
    node
}

fn parse_time(value: &str) -> Option<DateTime<Utc>> {
    DateTime::parse_from_rfc3339(value).ok().map(|t| t.with_timezone(&Utc))
}

/// C# `WorkDashboard.AgeLabel`.
pub fn age_label(locale: &Locale, date: DateTime<Utc>, now: DateTime<Utc>) -> String {
    let minutes = (now - date).num_minutes().max(0);
    let count = |n: i64| n.to_string();
    if minutes < 1 {
        locale.get("dashboard.age.now")
    } else if minutes < 60 {
        locale.get_with("dashboard.age.minutes", &[("count", &count(minutes))])
    } else if minutes < 1440 {
        locale.get_with("dashboard.age.hours", &[("count", &count(minutes / 60))])
    } else {
        locale.get_with("dashboard.age.days", &[("count", &count(minutes / 1440))])
    }
}

/// C# `WorkDashboard.FirstLine`: the first line, at most 200 text elements, leading space and trailing space dropped.
fn first_line(text: &str) -> String {
    let start = text.trim_start();
    let line = start.split(['\r', '\n', '\u{85}', '\u{2028}', '\u{2029}']).next().unwrap_or("");
    wire::text_elements(line, 200).0.trim_end().to_string()
}

/// The parts of C# `WorkDashboard.MakeCard` the frame reads: the last activity line, whether it failed, its tool, the last change.
struct Card {
    last: Option<String>,
    tool: Option<String>,
    is_error: bool,
    updated: Option<DateTime<Utc>>,
}

fn make_card(s: &RunSession) -> Card {
    let mut card = Card { last: None, tool: None, is_error: false, updated: None };
    for entry in s.logs().iter().rev() {
        let activity = entry.activity.as_ref().filter(|a| a.is_object());
        if entry.kind() == "image" && activity.is_none() {
            continue;
        }
        let text = |key: &str| activity.and_then(|a| a.get(key)).and_then(|v| v.as_str()).unwrap_or("");
        let line = match activity {
            Some(_) => [first_line(text("toolName")), first_line(text("summary"))]
                .into_iter()
                .filter(|p| !p.is_empty())
                .collect::<Vec<_>>()
                .join(" · "),
            None => first_line(entry.text()),
        };
        if line.is_empty() {
            continue;
        }
        card.last = Some(line);
        card.tool = activity.map(|_| first_line(text("toolName"))).filter(|t| !t.is_empty());
        card.is_error = match activity {
            Some(_) => text("state") == "error",
            None => entry.kind() == "error",
        };
        break;
    }
    let timestamp = s.logs().last().and_then(|e| e.timestamp.as_deref()).or(s.created_at());
    card.updated = timestamp.and_then(parse_time);
    card
}

/// C# `WorkDashboard.SidebarDetail` for a pane that is not running (a loaded state holds none: restore stops them).
fn sidebar_detail(locale: &Locale, s: &RunSession, card: &Card, now: DateTime<Utc>) -> String {
    if s.kind() != "claude" {
        return String::new();
    }
    let mut parts = vec![];
    if s.status() != "running" {
        if Tone::of(s.status()) == Tone::Err
            && card.is_error
            && let Some(activity) = &card.last
        {
            parts.push(match &card.tool {
                Some(tool) => locale.get_with("dashboard.card.toolFailed", &[("tool", tool)]),
                // C# Shorten(activity, 40): more than 40 text elements keep the first 39 and "…".
                None => match wire::text_elements(activity, 40) {
                    (_, false) => activity.clone(),
                    _ => format!("{}…", wire::text_elements(activity, 39).0),
                },
            });
        }
        if let Some(date) = card.updated {
            parts.push(age_label(locale, date, now));
        }
    }
    parts.join(" · ")
}

/// C# `MainWindow.SidebarKindWords`.
fn kind_words(locale: &Locale, kind: &str) -> String {
    match kind {
        "shell" => format!("{} · {}", locale.get("dashboard.kind.shell"), locale.get("phone.card.localTerminal")),
        "browser" => locale.get("browser.tab.title"),
        "agent-terminal" => locale.get("dashboard.kind.agentTerminal"),
        "agent-browser" => locale.get("dashboard.kind.agentBrowser"),
        "files" => locale.get("files.pane.title"),
        "workmap" => locale.get("workMap.title"),
        "claude" => locale.get("dashboard.kind.agent"),
        other => other.to_string(),
    }
}

/// C# `MainWindow.DashboardMeta` (the model's raw id where C# shows the catalogue's name).
fn dashboard_meta(locale: &Locale, s: &RunSession, detail: &str) -> String {
    match s.kind() {
        "shell" => format!("{} ·", locale.get("dashboard.kind.shell")),
        "browser" => locale.get("browser.tab.title"),
        "files" => locale.get("files.pane.title"),
        "workmap" => locale.get("workMap.title"),
        "agent-terminal" | "agent-browser" => format!(
            "{} · {}",
            locale.get(if s.kind() == "agent-terminal" { "dashboard.kind.agentTerminal" } else { "dashboard.kind.agentBrowser" }),
            provider_label(s.provider())
        ),
        "claude" => {
            let mut parts = vec![];
            let model = s.model().trim();
            if !model.is_empty() && model != "default" {
                parts.push(model.to_string());
            }
            if !detail.is_empty() {
                parts.push(detail.to_string());
            }
            if parts.is_empty() { String::new() } else { format!("· {}", parts.join(" · ")) }
        }
        other => other.to_string(),
    }
}

/// C# `MainWindow.WebPaneInfo` with no pane waiting on the user.
pub fn pane_info(locale: &Locale, s: &RunSession, now: DateTime<Utc>) -> PaneInfo {
    let shown = display_status(s.status(), 0);
    let tone = Tone::of(shown);
    let provider = sidebar_provider(s);
    let card = make_card(s);
    let detail = sidebar_detail(locale, s, &card, now);
    let help = if crate::normalize::follows_requests(s) {
        crate::normalize::tooltip(s).unwrap_or_else(|| s.title().to_string())
    } else {
        s.title().to_string()
    };
    PaneInfo {
        id: s.id().into(),
        workspace_id: s.workspace_id().into(),
        title: s.title().into(),
        help,
        kind: s.kind().into(),
        provider: s.provider().into(),
        beta: s.kind() == "claude" && is_beta(s.provider()),
        tone: tone.name(),
        word: locale.get(tone.word_key()),
        settled: matches!(tone, Tone::Done | Tone::Stop | Tone::Idle),
        agent: provider.map(str::to_string),
        agent_label: provider.map(|p| provider_label(p).to_string()),
        meta: match provider {
            None => kind_words(locale, s.kind()),
            Some(_) if detail.is_empty() => String::new(),
            Some(_) => format!("· {detail}"),
        },
        wait: None,
        tab_mark: is_counted(s.kind()) && tone != Tone::Idle,
        waiting: 0,
        dash_meta: dashboard_meta(locale, s, &detail),
        activity: if s.status() == "running" { card.last.clone() } else { None },
        activity_error: card.is_error,
        dash_wait: None,
    }
}

/// C# `RemoteWindowsStrings.HostWithOS` with the host's OS not known yet (no connection).
fn host_with_os(locale: &Locale, host: &str) -> String {
    locale.get_with("remote.hostWithOS", &[("host", host), ("os", &locale.get("remote.hostOS.unknown"))])
}

fn remote_host(w: &Workspace) -> Option<String> {
    w.remote().map(|r| r.host_name.clone().unwrap_or_default())
}

/// What the frame model is built from besides the state.
pub struct ShellContext<'a> {
    pub locale: Locale,
    pub version: &'a str,
    pub now: DateTime<Utc>,
    /// A banner line (C#'s `error` text): the load's notice or failure.
    pub error: Option<String>,
}

/// C# `MainWindow.WebShellModel`. `defaults` is the window's `layoutDefaults` (see [`effective_layout`]).
pub fn shell_model(state: &AppSnapshot, context: &ShellContext, defaults: &mut HashMap<String, Node>) -> ShellModel {
    let locale = &context.locale;
    let open = expanded(state);
    let active = state.active_workspace_id.as_deref().and_then(|id| state.workspace(id));
    let layout = active.and_then(|w| {
        let tree = effective_layout(state, w.id(), defaults)?;
        let mode = layout_mode(state, w.id());
        let root = if mode == "focus" {
            let groups = layout::groups(&tree);
            let active_id = state.active_session_id.as_deref().unwrap_or("");
            groups
                .iter()
                .find(|g| g.session_ids.iter().any(|s| s == active_id))
                .or(groups.first())
                .map(|g| (*g).clone())
                .unwrap_or(tree.clone())
        } else {
            tree
        };
        Some(LayoutModel { workspace_id: w.id().into(), mode, root })
    });
    let workspaces = state
        .workspaces
        .iter()
        .map(|w| {
            let is_open = open.contains(w.id());
            SidebarWorkspace {
                id: w.id().into(),
                name: w.name().into(),
                path: w.path().into(),
                remote_host: remote_host(w),
                local: w.remote().is_none(),
                counts: badges(state.sessions.iter().filter(|s| s.workspace_id() == w.id())),
                expanded: is_open,
                items: is_open.then(|| {
                    state
                        .sessions
                        .iter()
                        .filter(|s| s.workspace_id() == w.id())
                        .map(|s| SidebarItem { kind: "pane", id: s.id().into(), depth: 0 })
                        .collect()
                }),
            }
        })
        .collect();
    let header = active.map(|w| {
        let remote = w.remote().is_some();
        Header {
            id: w.id().into(),
            name: w.name().into(),
            path: w.path().into(),
            remote_host: remote_host(w).map(|h| host_with_os(locale, &h)),
            work_map_enabled: false,
            work_map_help: locale.get(if remote { "workMap.unavailable.remote" } else { "workMap.unavailable.notGit" }),
            files_enabled: !remote,
            files_help: if remote {
                locale.get("remote.windows.workspace.paneUnavailable")
            } else {
                format!("{} (Ctrl+Shift+E)", locale.get("menu.showFiles"))
            },
        }
    });
    let in_active = state.sessions.iter().filter(|s| Some(s.workspace_id()) == state.active_workspace_id.as_deref()).count();
    let running = state.sessions.iter().filter(|s| is_counted(s.kind()) && s.status() == "running").count();
    let machine = match active.and_then(|w| w.remote()) {
        Some(r) => locale.get_with("remote.status.notConnected", &[("host", &host_with_os(locale, r.host_name.as_deref().unwrap_or("")))]),
        None => locale.get("window.status.thisMachine"),
    };
    ShellModel {
        version: context.version.into(),
        dialog_open: false,
        panes: state.sessions.iter().map(|s| pane_info(locale, s, context.now)).collect(),
        entry: badges(state.sessions.iter()),
        workspaces,
        header,
        layout,
        error: context.error.clone(),
        status: Status {
            machine,
            remote: active.and_then(|w| w.remote()).map(|_| false),
            panes: locale.get_with("window.status.paneCount", &[("count", &in_active.to_string())]),
            running: locale.get_with("window.status.runningCount", &[("count", &running.to_string())]),
            update_help: locale.get("window.status.updateBadgeHelp"),
            setup: false,
            chips_title: locale.get("windows.accountUsage.title"),
            chips_tooltip: locale.get("windows.accountUsage.chipsTooltip"),
        },
        setup_needed: false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn tones_and_words() {
        let en = Locale::new("en");
        assert_eq!(Tone::of("running").name(), "run");
        assert_eq!(Tone::of("cancelled").name(), "stop");
        assert_eq!(en.get(Tone::of("whatever").word_key()), "Ready");
    }

    #[test]
    fn age_labels() {
        let en = Locale::new("en");
        let now = parse_time("2026-11-13T12:00:00Z").unwrap();
        assert_eq!(age_label(&en, parse_time("2026-11-13T11:59:30Z").unwrap(), now), "just now");
        assert_eq!(age_label(&en, parse_time("2026-11-13T11:15:00Z").unwrap(), now), "45 min ago");
        assert_eq!(age_label(&en, parse_time("2026-11-10T11:00:00.1234567+00:00").unwrap(), now), "3 days ago");
    }

    #[test]
    fn a_failed_tool_is_named_in_the_sidebar() {
        let en = Locale::new("en");
        let s: RunSession = serde_json::from_value(json!({
            "id": "s1", "workspaceId": "w1", "status": "error", "titleMode": "fixed", "title": "T",
            "logs": [{"id": "l1", "kind": "assistant", "text": "x", "timestamp": "2026-11-13T11:00:00Z",
                      "activity": {"toolName": "Bash", "summary": "npm test", "state": "error"}}]
        }))
        .unwrap();
        let info = pane_info(&en, &s, parse_time("2026-11-13T12:00:00Z").unwrap());
        assert_eq!(info.meta, "· Bash failed · 1 hr ago");
        assert_eq!(info.tone, "err");
        assert!(!info.settled);
        assert_eq!(info.agent_label.as_deref(), Some("Claude"));
    }
}
