//! The frame's model the page draws (`{kind:"shell", shell}`): C# `MainWindow.WebShellModel` (MightyCode.WinUI/
//! MainWindow.WebShell.Shell.cs:463) with every part it reads, in the same field order: the panes (`WebPaneInfo`, :348), the
//! counts (`WorkDashboard.WorkspaceBadges`), the sidebar (`WebSidebarItems`, :402, with the delegation tree of
//! `DelegationSidebar.Tree`), the header (git, work map, files), the layout (`EffectiveLayout`, MainWindow.Layout.cs:28), the
//! dashboard (`WebDashboard`, :444), the banners and the status bar (`RefreshStatusBar`, MainWindow.Shell.cs:294). Where C# reads
//! a WinUI control the value comes from the same rule here, through [`ShellContext`].
//!
//! Parts later stages fill in (a typed list, so none is forgotten): [`Pending`].

use std::collections::{HashMap, HashSet};

use chrono::{DateTime, Utc};
use serde::Serialize;

use crate::layout;
use crate::locale::Locale;
use crate::model::{AppSnapshot, PaneLayoutNode, RunSession, Workspace};
use crate::model_label::{self, provider_label};
use crate::normalize;
use crate::time::{self, DateTimeOffset};
use crate::wire;

/// What the model leaves at its resting value until the stage that has its source: each names the C# member it stands for.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Pending {
    /// `PendingRequests` / `DashboardAttention`: tool permissions and questions waiting on the user (W2, run engine). The host
    /// passes none yet: every pane's attention is 0.
    Attention,
    /// `DesktopService.SidebarTree` reads the delegation file (W7): no child rows yet, every pane is top level.
    DelegationChildren,
    /// `WebChildCard`'s busy state and notes (`IsCardBusy`, `CardNote`, `ChildClose`, W7).
    ChildCardNotes,
    /// `runtime` and `runtimeChecks` (`ProviderCatalog.GetRuntimeAsync`, W2): the machine words say "checking" until it is known.
    Runtime,
    /// `appUpdate` (W9): no update badge, staged badge or countdown.
    AppUpdate,
    /// `WebCompanionControls` (companion, W8).
    Companion,
    /// `SetupNeedsAttention` and the setup badge (setup catalogue, W6).
    Setup,
    /// `resourceBanner` (`CheckResources`, ResourceHealth, W1-A's platform layer).
    ResourceBanner,
    /// `RemoteConnection(workspace)` (remote client, W8): a remote workspace reads "not connected" with an unknown OS.
    RemoteConnections,
    /// `PaneRuntime(provider).ModelCatalog`: the dashboard's model names come from the fallback catalogue until the CLI's list is read.
    ModelCatalog,
}

pub const PENDING: [Pending; 10] = [
    Pending::Attention,
    Pending::DelegationChildren,
    Pending::ChildCardNotes,
    Pending::Runtime,
    Pending::AppUpdate,
    Pending::Companion,
    Pending::Setup,
    Pending::ResourceBanner,
    Pending::RemoteConnections,
    Pending::ModelCatalog,
];

// ── tones and counts (StatusGlyph, WorkDashboard) ───────────────────────────────────────────────────────────────────────

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
    fn rank(self) -> u8 {
        match self {
            Tone::Wait => 0,
            Tone::Run => 1,
            Tone::Err => 2,
            Tone::Done => 3,
            Tone::Stop => 4,
            Tone::Idle => 5,
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

/// C# `ProviderMark.SidebarProvider`: an agent pane's provider, when it is one the app knows.
pub fn sidebar_provider(s: &RunSession) -> Option<&str> {
    (s.kind() == "claude" && wire::PROVIDERS.contains(&s.provider())).then(|| s.provider())
}

/// C# `WorkDashboard.Attention`: what waits on the user in one pane.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Attention {
    pub questions: usize,
    pub permissions: usize,
}

impl Attention {
    pub fn total(&self) -> usize {
        self.questions + self.permissions
    }
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

/// C# `WorkDashboard.WorkspaceBadges`.
pub fn badges<'a>(sessions: impl Iterator<Item = &'a RunSession>, attention: &dyn Fn(&str) -> Attention) -> Counts {
    let mut counts = Counts::default();
    for pane in sessions.filter(|s| is_counted(s.kind())) {
        let pending = attention(pane.id());
        counts.questions += pending.questions;
        counts.permissions += pending.permissions;
        match Tone::of(display_status(pane.status(), pending.total())) {
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

// ── a pane's card (WorkDashboard.MakeCard) ──────────────────────────────────────────────────────────────────────────────

/// C# `WorkDashboard.Card`.
pub struct Card<'a> {
    pub session: &'a RunSession,
    pub attention: Attention,
    pub display_status: String,
    pub context_percent: Option<f64>,
    pub last_activity: Option<String>,
    pub updated_at: Option<DateTimeOffset>,
    pub activity_is_error: bool,
    pub tool: Option<String>,
}

impl Card<'_> {
    /// `Card.Model`: the pane's own model, not "default".
    pub fn model(&self) -> Option<&str> {
        let trimmed = self.session.model().trim();
        (!trimmed.is_empty() && trimmed != "default").then_some(trimmed)
    }
}

/// C# `WorkDashboard.FirstLine`: from the first character that is no white space, up to the first line break, at most 200 text
/// elements, trailing white space dropped.
fn first_line(text: &str) -> String {
    let start = text.find(|c: char| !c.is_whitespace()).unwrap_or(text.len());
    let rest = &text[start..];
    let mut end = 0;
    for (count, element) in unicode_segmentation::UnicodeSegmentation::graphemes(rest, true).enumerate() {
        if count == 200 || element.starts_with(['\r', '\n', '\u{85}', '\u{2028}', '\u{2029}']) {
            break;
        }
        end += element.len();
    }
    rest[..end].trim_end().to_string()
}

/// C# `SessionUsage.ContextPercent`.
fn context_percent(s: &RunSession) -> Option<f64> {
    let usage = s.session_usage.as_ref().filter(|u| u.provider == s.provider())?;
    let used = usage.context_used_tokens.filter(|u| *u >= 0)?;
    let window = usage.context_window_tokens.filter(|w| *w > 0)?;
    Some((used as f64 / window as f64 * 100.0).clamp(0.0, 100.0))
}

/// C# `WorkDashboard.MakeCard`.
pub fn make_card(session: &RunSession, attention: Attention) -> Card<'_> {
    let mut card = Card {
        session,
        attention,
        display_status: if attention.total() > 0 { "waiting".into() } else { session.status().to_string() },
        context_percent: context_percent(session).filter(|p| p.is_finite()).map(|p| p.clamp(0.0, 100.0)),
        last_activity: None,
        updated_at: None,
        activity_is_error: false,
        tool: None,
    };
    for entry in session.logs().iter().rev() {
        if entry.kind() == "image" && entry.activity.is_none() {
            continue;
        }
        let line = match &entry.activity {
            Some(a) => [first_line(a.tool_name.as_deref().unwrap_or("")), first_line(&a.summary)]
                .into_iter()
                .filter(|p| !p.is_empty())
                .collect::<Vec<_>>()
                .join(" · "),
            None => first_line(entry.text()),
        };
        if line.is_empty() {
            continue;
        }
        card.last_activity = Some(line);
        card.tool = entry.activity.as_ref().and_then(|a| a.tool_name.as_deref()).map(first_line).filter(|t| !t.is_empty());
        card.activity_is_error = match &entry.activity {
            Some(activity) => activity.state == "error",
            None => entry.kind() == "error",
        };
        break;
    }
    let timestamp = session.logs().last().map(|e| e.timestamp.as_deref()).unwrap_or(session.created_at());
    card.updated_at = time::parse(timestamp);
    card
}

/// C# `WorkDashboard.AgeLabel`.
pub fn age_label(locale: &Locale, date: DateTimeOffset, now: DateTimeOffset) -> String {
    let minutes = ((now.utc_ticks() - date.utc_ticks()) as f64 / 600_000_000.0).clamp(0.0, i32::MAX as f64) as i64;
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

/// C# `AgentRunTiming.Label(now)`: `mm:ss` (or `h:mm:ss`), "about" when approximate.
pub fn timing_label(locale: &Locale, timing: &crate::model::AgentRunTiming, now: DateTimeOffset) -> String {
    let elapsed = if timing.is_valid() {
        let end = timing.finished_at.unwrap_or(now);
        ((end.utc_ticks() - timing.started_at.utc_ticks()) as f64 / 10_000_000.0).max(0.0)
    } else {
        0.0
    };
    let seconds = elapsed as i64;
    let label = if seconds >= 3600 {
        format!("{}:{:02}:{:02}", seconds / 3600, seconds / 60 % 60, seconds % 60)
    } else {
        format!("{:02}:{:02}", seconds / 60, seconds % 60)
    };
    if timing.is_approximate { locale.get("dashboard.card.approximate").replace("{time}", &label) } else { label }
}

/// C# `WorkDashboard.Shorten`: more than `maximum` text elements keep the first `maximum - 1` and "…".
fn shorten(value: &str, maximum: usize) -> String {
    match wire::text_elements(value, maximum) {
        (_, false) => value.to_string(),
        _ => format!("{}…", wire::text_elements(value, maximum - 1).0),
    }
}

/// C# `WorkDashboard.SidebarDetail`.
pub fn sidebar_detail(locale: &Locale, card: &Card, now: DateTimeOffset) -> String {
    if card.session.kind() != "claude" {
        return String::new();
    }
    let mut parts = vec![];
    if card.session.status() == "running" {
        if let Some(timing) = card.session.run_timing.as_ref().filter(|t| t.is_valid()) {
            parts.push(timing_label(locale, timing, now));
        }
        if let Some(percent) = card.context_percent {
            // Math.Round(percent, MidpointRounding.AwayFromZero), written as .NET writes the whole double.
            parts.push(locale.get_with("phone.card.context", &[("percent", &crate::json::format_double(percent.round()))]));
        }
    } else {
        if Tone::of(&card.display_status) == Tone::Err
            && card.activity_is_error
            && let Some(activity) = &card.last_activity
        {
            parts.push(match &card.tool {
                Some(tool) => locale.get_with("dashboard.card.toolFailed", &[("tool", tool)]),
                None => shorten(activity, 40),
            });
        }
        if let Some(date) = card.updated_at {
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

/// C# `MainWindow.DashboardMeta` (MainWindow.DashboardWorkspaces.cs:61); the model named from the fallback catalogue until the
/// CLI's own list is read ([`Pending::ModelCatalog`]).
fn dashboard_meta(locale: &Locale, card: &Card, now: DateTimeOffset) -> String {
    let s = card.session;
    match s.kind() {
        "shell" => format!("{} ·", locale.get("dashboard.kind.shell")),
        "browser" => locale.get("browser.tab.title"),
        "files" => locale.get("files.pane.title"),
        "workmap" => locale.get("workMap.title"),
        kind @ ("agent-terminal" | "agent-browser") => format!(
            "{} · {}",
            locale.get(if kind == "agent-terminal" { "dashboard.kind.agentTerminal" } else { "dashboard.kind.agentBrowser" }),
            provider_label(s.provider())
        ),
        "claude" => {
            let mut parts = vec![];
            if card.model().is_some() {
                parts.push(model_label::selection(s, &model_label::fallback_models(s.provider(), locale), locale));
            }
            let detail = sidebar_detail(locale, card, now);
            if !detail.is_empty() {
                parts.push(detail);
            }
            if parts.is_empty() { String::new() } else { format!("· {}", parts.join(" · ")) }
        }
        other => other.to_string(),
    }
}

/// C# `SidebarWaitText`: questions first, then permissions.
fn wait_text(locale: &Locale, attention: Attention) -> Option<String> {
    if attention.questions > 0 {
        Some(locale.get_with("phone.card.questions", &[("count", &attention.questions.to_string())]))
    } else if attention.permissions > 0 {
        Some(locale.get_with("phone.card.permissions", &[("count", &attention.permissions.to_string())]))
    } else {
        None
    }
}

/// C# `DashboardAttentionWord`.
fn attention_word(locale: &Locale, attention: Attention) -> String {
    if attention.questions > 0 {
        locale.get_with("phone.card.questions", &[("count", &attention.questions.to_string())])
    } else {
        locale.get_with("phone.card.permissions", &[("count", &attention.permissions.to_string())])
    }
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

/// C# `MainWindow.WebPaneInfo`.
pub fn pane_info(locale: &Locale, s: &RunSession, attention: Attention, now: DateTimeOffset) -> PaneInfo {
    let shown = display_status(s.status(), attention.total());
    let tone = Tone::of(shown);
    let provider = sidebar_provider(s);
    let card = make_card(s, attention);
    let detail = sidebar_detail(locale, &card, now);
    let help =
        if normalize::follows_requests(s) { normalize::tooltip(s).unwrap_or_else(|| s.title().to_string()) } else { s.title().to_string() };
    PaneInfo {
        id: s.id().into(),
        workspace_id: s.workspace_id().into(),
        title: s.title().into(),
        help,
        kind: s.kind().into(),
        provider: s.provider().into(),
        beta: s.kind() == "claude" && is_beta(s.provider()),
        tone: tone.name(),
        word: locale.get(Tone::of(shown).word_key()),
        settled: matches!(tone, Tone::Done | Tone::Stop | Tone::Idle),
        agent: provider.map(str::to_string),
        agent_label: provider.map(|p| provider_label(p).to_string()),
        meta: match provider {
            None => kind_words(locale, s.kind()),
            Some(_) if detail.is_empty() => String::new(),
            Some(_) => format!("· {detail}"),
        },
        wait: wait_text(locale, attention),
        tab_mark: is_counted(s.kind()) && tone != Tone::Idle,
        waiting: attention.total(),
        dash_meta: dashboard_meta(locale, &card, now),
        activity: if s.status() == "running" { card.last_activity.clone() } else { None },
        activity_error: card.activity_is_error,
        dash_wait: (attention.total() > 0).then(|| attention_word(locale, attention)),
    }
}

// ── git (WorkspaceGitInfo) ──────────────────────────────────────────────────────────────────────────────────────────────

/// C# `WorkspaceGitInfo`: local Git status, read with `git status --porcelain=v2 --branch` (the host runs it).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WorkspaceGitInfo {
    pub branch: String,
    pub revision: Option<String>,
    pub is_dirty: bool,
    pub ahead: Option<i32>,
    pub behind: Option<i32>,
}

impl WorkspaceGitInfo {
    /// `Label`.
    pub fn label(&self) -> String {
        if self.branch == "(detached)" {
            format!("HEAD · {}", self.revision.as_deref().filter(|r| r.len() >= 7).map(|r| &r[..7]).unwrap_or("—"))
        } else {
            self.branch.clone()
        }
    }

    /// `Parse`: porcelain v2's branch headers and whether any entry is listed.
    pub fn parse(output: &str) -> Option<WorkspaceGitInfo> {
        let (mut branch, mut revision, mut ahead, mut behind, mut dirty) = (None, None, None, None, false);
        for line in output.split('\n') {
            let clean = line.trim_end_matches('\r');
            if let Some(head) = clean.strip_prefix("# branch.head ") {
                branch = Some(head.to_string());
            } else if let Some(oid) = clean.strip_prefix("# branch.oid ") {
                if (7..=64).contains(&oid.len()) && oid.bytes().all(|b| b.is_ascii_hexdigit()) {
                    revision = Some(oid.to_string());
                }
            } else if let Some(ab) = clean.strip_prefix("# branch.ab ") {
                let mut parts = ab.split(' ');
                if let (Some(a), Some(b), None) = (parts.next(), parts.next(), parts.next())
                    && let (Some(a), Some(b)) = (a.strip_prefix('+'), b.strip_prefix('-'))
                    && a.bytes().all(|c| c.is_ascii_digit())
                    && b.bytes().all(|c| c.is_ascii_digit())
                    && let (Ok(a), Ok(b)) = (a.parse(), b.parse())
                {
                    ahead = Some(a);
                    behind = Some(b);
                }
            } else if ["1 ", "2 ", "u ", "? "].iter().any(|p| clean.starts_with(p)) {
                dirty = true;
            }
        }
        let branch: String = branch.filter(|b| !b.is_empty() && b.len() <= 4096 && !b.chars().any(|c| c.is_control()))?;
        Some(WorkspaceGitInfo { branch, revision, is_dirty: dirty, ahead, behind })
    }
}

#[derive(Debug, Clone, Serialize, PartialEq)]
pub struct GitModel {
    pub label: String,
    pub dirty: bool,
    pub ahead: i32,
    pub behind: i32,
    pub help: String,
}

/// C# `WebGit`.
fn git_model(locale: &Locale, info: Option<&WorkspaceGitInfo>) -> Option<GitModel> {
    let info = info?;
    let label = info.label();
    Some(GitModel {
        help: format!(
            "Git · {label} · {}\n{}",
            locale.get(if info.is_dirty { "git.dirty" } else { "git.clean" }),
            locale.get("git.localUpstream")
        ),
        label,
        dirty: info.is_dirty,
        ahead: info.ahead.unwrap_or(0),
        behind: info.behind.unwrap_or(0),
    })
}

// ── delegation (DelegationSidebar) ──────────────────────────────────────────────────────────────────────────────────────

/// C# `ChildState`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ChildState {
    Creating,
    Running,
    Waiting,
    Reported,
    Ended,
    Interrupted,
    Merged,
    Failed,
    Closed,
    Discarded,
}

impl ChildState {
    fn word_key(self) -> &'static str {
        match self {
            ChildState::Creating => "delegation.child.state.creating",
            ChildState::Running => "delegation.child.state.running",
            ChildState::Waiting => "delegation.child.state.waiting",
            ChildState::Reported => "delegation.child.state.reported",
            ChildState::Merged => "delegation.child.state.merged",
            ChildState::Ended => "delegation.child.state.ended",
            ChildState::Interrupted => "delegation.child.state.interrupted",
            ChildState::Failed => "delegation.child.state.failed",
            ChildState::Closed => "delegation.child.state.closed",
            ChildState::Discarded => "delegation.child.state.discarded",
        }
    }

    /// `DelegationSidebar.Tone`.
    fn tone(self) -> Tone {
        match self {
            ChildState::Creating | ChildState::Running => Tone::Run,
            ChildState::Waiting => Tone::Wait,
            ChildState::Reported | ChildState::Merged => Tone::Done,
            ChildState::Failed => Tone::Err,
            ChildState::Interrupted => Tone::Stop,
            _ => Tone::Idle,
        }
    }
}

/// C# `DelegationCardAction`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CardAction {
    Merge,
    Undo,
    Discard,
}

impl CardAction {
    fn name(self) -> &'static str {
        match self {
            CardAction::Merge => "merge",
            CardAction::Undo => "undo",
            CardAction::Discard => "discard",
        }
    }
    fn label_key(self) -> &'static str {
        match self {
            CardAction::Merge => "delegation.card.merge",
            CardAction::Undo => "delegation.card.undo",
            CardAction::Discard => "delegation.card.discard",
        }
    }
}

/// C# `DelegationChildRow`: one delegated child as the sidebar lists it.
#[derive(Debug, Clone, PartialEq)]
pub struct DelegationChildRow {
    pub id: String,
    pub parent_session_id: String,
    pub state: ChildState,
    pub task: Option<String>,
    pub parent_branch: String,
    pub parent_checkout: Option<String>,
    pub can_undo: bool,
    pub held_notices: usize,
    pub started_by_human: bool,
    pub agent: String,
}

impl DelegationChildRow {
    /// `CardActions`.
    pub fn card_actions(&self) -> Vec<CardAction> {
        let mut actions = vec![];
        if matches!(self.state, ChildState::Reported | ChildState::Running | ChildState::Waiting) {
            actions.push(CardAction::Merge);
        }
        if self.can_undo {
            actions.push(CardAction::Undo);
        }
        if self.state != ChildState::Discarded {
            actions.push(CardAction::Discard);
        }
        actions
    }

    fn branch(&self, locale: &Locale) -> String {
        if self.parent_branch.is_empty() { locale.get("delegation.card.parentBranch") } else { self.parent_branch.clone() }
    }

    /// `DelegationText.ActionHelp`.
    fn action_help(&self, action: CardAction, locale: &Locale) -> String {
        match action {
            CardAction::Merge => locale.get_with(
                if self.state == ChildState::Reported { "delegation.card.mergeHelp" } else { "delegation.card.mergeLaterHelp" },
                &[("branch", &self.branch(locale))],
            ),
            CardAction::Undo => locale.get_with("delegation.card.undoHelp", &[("branch", &self.branch(locale))]),
            CardAction::Discard => locale.get_with("delegation.card.discardHelp", &[("branch", &format!("mighty/{}", self.id))]),
        }
    }
}

/// `DelegationPaths.Standardized` and `Same`/`IsInside` the Windows way (case and separators do not matter).
fn standardized(path: &str) -> String {
    let unified = path.replace('/', "\\");
    let trimmed = unified.trim_end_matches('\\');
    if trimmed.len() <= 2 { unified } else { trimmed.to_string() }
}

fn same_path(a: &str, b: &str) -> bool {
    a.to_lowercase() == b.to_lowercase()
}

fn is_inside(path: &str, folder: &str) -> bool {
    let folder = if folder.ends_with('\\') { folder.to_string() } else { format!("{folder}\\") };
    path.to_lowercase().starts_with(&folder.to_lowercase())
}

/// C# `DelegationTree`: the top panes, each open parent's children, and each closed parent's node (id, branch, children).
pub struct DelegationTree<'a> {
    pub top: Vec<&'a RunSession>,
    pub children: HashMap<String, Vec<&'a DelegationChildRow>>,
    pub parent_closed: Vec<(String, String, Vec<&'a DelegationChildRow>)>,
}

/// C# `DelegationSidebar.ListedWorkspaceId`.
fn listed_workspace_id(child: &DelegationChildRow, workspaces: &[Workspace], panes: &HashMap<&str, &RunSession>) -> Option<String> {
    if let Some(pane) = panes.get(child.id.as_str()) {
        return Some(pane.workspace_id().to_string());
    }
    let checkout = standardized(child.parent_checkout.as_deref()?);
    let paths: Vec<(&str, String)> = workspaces.iter().filter(|w| w.remote.is_none()).map(|w| (w.id(), standardized(w.path()))).collect();
    paths
        .iter()
        .find(|(_, p)| same_path(p, &checkout))
        .or_else(|| paths.iter().find(|(_, p)| is_inside(p, &checkout)))
        .map(|(id, _)| id.to_string())
}

/// C# `DelegationSidebar.Tree`: each child under its open parent pane, else under its closed parent's node.
pub fn delegation_tree<'a>(workspace_id: &str, state: &'a AppSnapshot, children: &'a [DelegationChildRow]) -> DelegationTree<'a> {
    let mut panes: HashMap<&str, &RunSession> = HashMap::new();
    for s in state.sessions() {
        panes.entry(s.id()).or_insert(s);
    }
    let mut open: HashMap<String, Vec<&DelegationChildRow>> = HashMap::new();
    let mut closed: Vec<(String, String, Vec<&DelegationChildRow>)> = vec![];
    let mut listed = HashSet::new();
    for child in children {
        if let Some(parent) = panes.get(child.parent_session_id.as_str()) {
            if parent.workspace_id() != workspace_id {
                continue;
            }
            open.entry(parent.id().to_string()).or_default().push(child);
        } else {
            if listed_workspace_id(child, state.workspaces(), &panes).as_deref() != Some(workspace_id) {
                continue;
            }
            match closed.iter_mut().find(|node| node.0 == child.parent_session_id) {
                Some(node) => node.2.push(child),
                None => closed.push((child.parent_session_id.clone(), child.parent_branch.clone(), vec![child])),
            }
        }
        listed.insert(child.id.as_str());
    }
    DelegationTree {
        top: state.sessions().iter().map(|s| &**s).filter(|s| s.workspace_id() == workspace_id && !listed.contains(s.id())).collect(),
        children: open,
        parent_closed: closed,
    }
}

#[derive(Debug, Clone, Serialize, PartialEq)]
pub struct ActionModel {
    pub action: &'static str,
    pub label: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub help: Option<String>,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ChildCard {
    pub busy: bool,
    pub actions: Vec<ActionModel>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub held: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub note: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub note_tone: Option<&'static str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub kept: Option<String>,
}

/// One row of an expanded workspace's list: a pane, a delegated child, or a closed parent's node.
#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(untagged)]
pub enum SidebarItem {
    Pane {
        #[serde(rename = "type")]
        kind: &'static str,
        id: String,
        depth: usize,
    },
    Child {
        #[serde(rename = "type")]
        kind: &'static str,
        id: String,
        depth: usize,
        title: String,
        tone: &'static str,
        #[serde(rename = "stateWord")]
        state_word: String,
        #[serde(rename = "stateTone")]
        state_tone: &'static str,
        #[serde(skip_serializing_if = "Option::is_none")]
        card: Option<ChildCard>,
        menu: Vec<ActionModel>,
    },
    ParentClosed {
        #[serde(rename = "type")]
        kind: &'static str,
        id: String,
        depth: usize,
        branch: String,
        count: usize,
        help: String,
        label: String,
        name: String,
    },
}

/// C# `WebChildCard` (busy and notes are [`Pending::ChildCardNotes`]).
fn child_card(locale: &Locale, child: &DelegationChildRow, held_notices: usize) -> Option<ChildCard> {
    let actions = child.card_actions();
    if actions.is_empty() && held_notices == 0 {
        return None;
    }
    Some(ChildCard {
        busy: false,
        actions: actions
            .iter()
            .map(|a| ActionModel { action: a.name(), label: locale.get(a.label_key()), help: Some(child.action_help(*a, locale)) })
            .collect(),
        held: (held_notices > 0).then(|| locale.get_with("delegation.card.heldNotices", &[("count", &held_notices.to_string())])),
        note: None,
        note_tone: None,
        kept: None,
    })
}

/// C# `WebChildItem`.
fn child_item(
    locale: &Locale,
    child: &DelegationChildRow,
    state: &AppSnapshot,
    parent_closed: bool,
    attention: &dyn Fn(&str) -> Attention,
) -> SidebarItem {
    let open = state.sessions().iter().any(|s| s.id() == child.id);
    let shown =
        if child.state == ChildState::Running && open && attention(&child.id).total() > 0 { ChildState::Waiting } else { child.state };
    SidebarItem::Child {
        kind: if open { "pane" } else { "closedChild" },
        id: child.id.clone(),
        depth: 1,
        title: child.task.clone().unwrap_or_else(|| locale.get("delegation.child.untitled")),
        tone: child.state.tone().name(),
        state_word: locale.get(shown.word_key()),
        state_tone: shown.tone().name(),
        card: child_card(locale, child, if parent_closed { child.held_notices } else { 0 }),
        menu: child.card_actions().iter().map(|a| ActionModel { action: a.name(), label: locale.get(a.label_key()), help: None }).collect(),
    }
}

/// C# `WebSidebarItems`: each pane, its delegated children under it, then the closed parents' nodes.
fn sidebar_items(
    locale: &Locale,
    state: &AppSnapshot,
    workspace_id: &str,
    children: &[DelegationChildRow],
    attention: &dyn Fn(&str) -> Attention,
) -> Vec<SidebarItem> {
    let tree = delegation_tree(workspace_id, state, children);
    let mut items = vec![];
    for session in &tree.top {
        items.push(SidebarItem::Pane { kind: "pane", id: session.id().into(), depth: 0 });
        for child in tree.children.get(session.id()).into_iter().flatten() {
            items.push(child_item(locale, child, state, false, attention));
        }
    }
    for (id, branch, rows) in &tree.parent_closed {
        items.push(SidebarItem::ParentClosed {
            kind: "parentClosed",
            id: id.clone(),
            depth: 0,
            branch: branch.clone(),
            count: rows.len(),
            help: locale.get("delegation.tree.parentClosedHelp"),
            label: locale.get("delegation.tree.parentClosed"),
            name: locale.get_with("delegation.tree.parentClosedAccessibility", &[("count", &rows.len().to_string())]),
        });
        for child in rows {
            items.push(child_item(locale, child, state, true, attention));
        }
    }
    items
}

// ── the model ───────────────────────────────────────────────────────────────────────────────────────────────────────────

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
    pub git: Option<GitModel>,
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
    pub root: PaneLayoutNode,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct DashboardWorkspace {
    pub id: String,
    pub name: String,
    pub path: String,
    pub local: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub git: Option<GitModel>,
    pub rows: Vec<String>,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
pub struct Dashboard {
    pub subtitle: String,
    pub running: usize,
    pub waiting: usize,
    pub done: usize,
    /// The usage card's rows (`usage.LeadingBars()`), none when no provider has a window.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub usage: Option<Vec<UsageBars>>,
    pub workspaces: Vec<DashboardWorkspace>,
}

// ── account usage (AccountUsageStatus as the page draws it, MainWindow.WebShell.Shell.cs:422-452, :508-511) ─────────────────

/// One word of a usage chip: a leading window in its own ink (a window at 90% or more warns).
#[derive(Debug, Clone, Serialize, PartialEq)]
pub struct UsageWord {
    pub text: String,
    pub warning: bool,
}

/// A status bar chip: its provider, the name a screen reader hears, its words.
#[derive(Debug, Clone, Serialize, PartialEq)]
pub struct UsageChip {
    pub provider: String,
    pub name: String,
    pub words: Vec<UsageWord>,
}

/// One window of a popover card.
#[derive(Debug, Clone, Serialize, PartialEq)]
pub struct UsageWindowRow {
    pub label: String,
    pub used: String,
    pub fraction: f64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reset: Option<String>,
    pub warning: bool,
}

/// One 리셋권 row of the Claude card.
#[derive(Debug, Clone, Serialize, PartialEq)]
pub struct UsageResetRow {
    pub program: String,
    pub label: String,
    pub text: String,
}

/// The Claude card's 리셋권 rows and their link.
#[derive(Debug, Clone, Serialize, PartialEq)]
pub struct UsageReset {
    pub title: String,
    pub rows: Vec<UsageResetRow>,
    pub link: String,
    pub url: String,
}

/// A provider's card in the usage popover.
#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct UsageCard {
    pub provider: String,
    pub name: String,
    pub title: String,
    pub beta: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub account: Option<String>,
    pub windows: Vec<UsageWindowRow>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub detail: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub note: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub checked_at: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reset: Option<UsageReset>,
}

/// The direct Claude lookup's switch beside its words.
#[derive(Debug, Clone, Serialize, PartialEq)]
pub struct UsageDirect {
    pub label: String,
    pub description: String,
    pub on: bool,
}

/// The usage popover (`WebUsageDetails`).
#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct UsageDetails {
    pub title: String,
    pub refreshing: bool,
    pub refresh_label: String,
    pub refresh_tooltip: String,
    pub cards: Vec<UsageCard>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub direct: Option<UsageDirect>,
    pub shared: String,
}

/// One bar of the dashboard's usage card.
#[derive(Debug, Clone, Serialize, PartialEq)]
pub struct UsageBar {
    pub label: String,
    pub percent: String,
    pub fraction: f64,
    pub warning: bool,
}

/// A provider's bars on the dashboard's usage card.
#[derive(Debug, Clone, Serialize, PartialEq)]
pub struct UsageBars {
    pub provider: String,
    pub name: String,
    pub bars: Vec<UsageBar>,
}

/// What the account usage gives the frame: the chips (none while no provider has an AI pane), the popover and the dashboard's bars.
#[derive(Debug, Clone, PartialEq)]
pub struct UsageModel {
    pub chips: Option<Vec<UsageChip>>,
    pub details: UsageDetails,
    pub bars: Vec<UsageBars>,
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
    #[serde(skip_serializing_if = "Option::is_none")]
    pub chips: Option<Vec<UsageChip>>,
    pub chips_title: String,
    pub chips_tooltip: String,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ShellModel {
    pub version: String,
    pub dialog_open: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub dashboard: Option<Dashboard>,
    pub panes: Vec<PaneInfo>,
    pub entry: Counts,
    pub workspaces: Vec<SidebarWorkspace>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub header: Option<Header>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub layout: Option<LayoutModel>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub resource: Option<String>,
    pub status: Status,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub usage: Option<UsageDetails>,
    pub setup_needed: bool,
}

/// What a remote workspace's connection says (`RemoteConnectionInfo`): connected, and the host's OS from its last answer.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct RemoteConnection {
    pub connected: bool,
    pub platform: Option<String>,
}

/// The runtime check's state (`runtime`, `runtimeChecks`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Runtime {
    /// No runtime read yet: "checking the runtime".
    Unknown,
    /// A check (runtime and models) is running.
    Checking,
    /// Read: "this PC".
    Known,
}

/// What the frame model is built from besides the state: the window's own state, which C# keeps in its controls.
pub struct ShellContext<'a> {
    pub locale: Locale,
    pub version: &'a str,
    pub now: DateTime<Utc>,
    /// The error banner (C#'s `error.Text`): the load's notice or failure, a failed save.
    pub error: Option<String>,
    pub resource: Option<String>,
    pub dialog_open: bool,
    pub shows_dashboard: bool,
    pub attention: &'a HashMap<String, Attention>,
    /// The active workspace's git, read for its folder (`gitInfo` while `gitWorkspaceKey` is the active workspace's).
    pub git: Option<&'a WorkspaceGitInfo>,
    pub dashboard_git: &'a HashMap<String, WorkspaceGitInfo>,
    pub children: &'a [DelegationChildRow],
    pub runtime: Runtime,
    pub setup_badge: bool,
    pub setup_needed: bool,
    pub remote: &'a HashMap<String, RemoteConnection>,
    /// The account usage's chips, popover and bars (the host's `AccountUsageStatus`), when it is there.
    pub usage: Option<&'a UsageModel>,
}

/// C# `WorkspaceDisclosure.Expanded`: the saved list, or the active workspace when none was saved.
pub fn expanded(state: &AppSnapshot) -> HashSet<String> {
    let listed: Vec<String> = match state.expanded_workspace_ids.as_ref() {
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
    list.sort_by(|a, b| normalize::ordinal(a, b));
    Some(list)
}

/// C# `WorkspaceDisclosure.Open`.
pub fn open_disclosure(state: &AppSnapshot, id: &str) -> Option<Vec<String>> {
    state.workspace(id)?;
    let mut ids = expanded(state);
    ids.insert(id.to_string());
    let mut list: Vec<String> = ids.into_iter().collect();
    list.sort_by(|a, b| normalize::ordinal(a, b));
    Some(list)
}

/// C# `MainWindow.LayoutMode`.
pub fn layout_mode(state: &AppSnapshot, workspace: &str) -> String {
    match state.pane_layout_modes.as_ref().and_then(|m| m.get(workspace)) {
        Some(mode) => mode.clone().unwrap_or_default(),
        None => state.layout().to_string(),
    }
}

/// C# `MainWindow.EffectiveLayout`: the saved tree, else the one made before (kept so its ids stay put), else the mode's preset;
/// normalized to the workspace's panes, the active one selected. `defaults` is the window's `layoutDefaults`.
pub fn effective_layout(state: &AppSnapshot, workspace_id: &str, defaults: &mut HashMap<String, PaneLayoutNode>) -> Option<PaneLayoutNode> {
    let ids = state.pane_ids(workspace_id);
    let active_here = state.active_workspace_id.as_deref() == Some(workspace_id);
    let saved = match state.pane_layouts.as_ref().and_then(|l| l.get(workspace_id)) {
        Some(node) => node.clone(),
        None => defaults.get(workspace_id).cloned(),
    };
    let node = saved.or_else(|| {
        let preferred = if active_here {
            state.active_session_id.clone()
        } else {
            state.pane_layout_active_session_ids.as_ref().and_then(|m| m.get(workspace_id)).cloned().flatten()
        };
        layout::preset(&ids, &layout_mode(state, workspace_id), preferred.as_deref())
    });
    let node = layout::normalize(node.as_ref(), &ids, if active_here { state.active_session_id.as_deref() } else { None });
    if let Some(node) = &node {
        defaults.insert(workspace_id.to_string(), node.clone());
    }
    node
}

/// `RemoteWindowsStrings.HostOS` / `HostWithOS`.
fn host_with_os(locale: &Locale, host: &str, platform: Option<&str>) -> String {
    let os = locale.get(match platform {
        Some("darwin") => "remote.hostOS.darwin",
        Some("win32") => "remote.hostOS.win32",
        Some("linux") => "remote.hostOS.linux",
        _ => "remote.hostOS.unknown",
    });
    locale.get_with("remote.hostWithOS", &[("host", host), ("os", &os)])
}

/// C# `WorkDashboard.Ordered`: waiting, running, failed, done, stopped, idle; the newest first within each.
pub fn ordered<'a>(mut cards: Vec<Card<'a>>) -> Vec<Card<'a>> {
    cards.sort_by(|a, b| {
        let rank = Tone::of(&a.display_status).rank().cmp(&Tone::of(&b.display_status).rank());
        let newer = match (a.updated_at, b.updated_at) {
            (Some(x), Some(y)) => y.utc_ticks().cmp(&x.utc_ticks()),
            (Some(_), None) => std::cmp::Ordering::Less,
            (None, Some(_)) => std::cmp::Ordering::Greater,
            (None, None) => std::cmp::Ordering::Equal,
        };
        rank.then(newer)
    });
    cards
}

/// C# `MainWindow.WebShellModel`. `defaults` is the window's `layoutDefaults` (see [`effective_layout`]).
pub fn shell_model(state: &AppSnapshot, context: &ShellContext, defaults: &mut HashMap<String, PaneLayoutNode>) -> ShellModel {
    let locale = &context.locale;
    let now = DateTimeOffset::from_utc_ticks(time::ticks_of(context.now));
    let attention = |id: &str| context.attention.get(id).copied().unwrap_or_default();
    let open = expanded(state);
    let active = state.active_workspace_id.as_deref().and_then(|id| state.workspace(id));
    let layout = active.and_then(|w| {
        let tree = effective_layout(state, w.id(), defaults)?;
        let mode = layout_mode(state, w.id());
        let root = if mode == "focus" {
            let groups = layout::groups(&tree);
            let active_id = state.active_session_id.as_deref().unwrap_or("");
            groups.iter().find(|g| g.session_ids().any(|s| s == active_id)).or(groups.first()).map(|g| (*g).clone()).unwrap_or(tree.clone())
        } else {
            tree
        };
        Some(LayoutModel { workspace_id: w.id().into(), mode, root })
    });
    let sessions: Vec<&RunSession> = state.sessions().iter().map(|s| &**s).collect();
    let dashboard = context.shows_dashboard.then(|| {
        let counted: Vec<&&RunSession> = sessions.iter().filter(|s| is_counted(s.kind())).collect();
        Dashboard {
            subtitle: locale.get_with(
                "dashboard.subtitle",
                &[("workspaces", &state.workspaces().len().to_string()), ("panes", &sessions.len().to_string())],
            ),
            running: counted.iter().filter(|s| s.status() == "running").count(),
            waiting: counted.iter().map(|s| attention(s.id()).total()).sum(),
            done: counted.iter().filter(|s| s.status() == "completed").count(),
            usage: context.usage.map(|u| u.bars.clone()).filter(|bars| !bars.is_empty()),
            workspaces: state
                .workspaces()
                .iter()
                .map(|w| DashboardWorkspace {
                    id: w.id().into(),
                    name: w.name().into(),
                    path: w.path().into(),
                    local: w.remote.is_none(),
                    git: git_model(locale, context.dashboard_git.get(w.id()).filter(|_| w.remote.is_none())),
                    rows: ordered(
                        sessions.iter().filter(|s| s.workspace_id() == w.id()).map(|s| make_card(s, attention(s.id()))).collect(),
                    )
                    .iter()
                    .map(|c| c.session.id().to_string())
                    .collect(),
                })
                .collect(),
        }
    });
    let workspaces = state
        .workspaces()
        .iter()
        .map(|w| {
            let is_open = open.contains(w.id());
            SidebarWorkspace {
                id: w.id().into(),
                name: w.name().into(),
                path: w.path().into(),
                remote_host: w.remote.as_ref().map(|r| r.host_name.clone().unwrap_or_default()),
                local: w.remote.is_none(),
                counts: badges(sessions.iter().copied().filter(|s| s.workspace_id() == w.id()), &attention),
                expanded: is_open,
                items: is_open.then(|| sidebar_items(locale, state, w.id(), context.children, &attention)),
            }
        })
        .collect();
    let connection = |w: &Workspace| w.remote.as_ref().and_then(|r| context.remote.get(r.connection_id.as_deref().unwrap_or("")));
    let header = active.map(|w| {
        let remote = w.remote.is_some();
        let git = if remote { None } else { context.git };
        Header {
            id: w.id().into(),
            name: w.name().into(),
            path: w.path().into(),
            git: git_model(locale, git),
            remote_host: w
                .remote
                .as_ref()
                .map(|r| host_with_os(locale, r.host_name.as_deref().unwrap_or(""), connection(w).and_then(|c| c.platform.as_deref()))),
            work_map_enabled: !remote && git.is_some(),
            work_map_help: locale.get(if remote {
                "workMap.unavailable.remote"
            } else if git.is_none() {
                "workMap.unavailable.notGit"
            } else {
                "workMap.open"
            }),
            files_enabled: !remote,
            files_help: if remote {
                locale.get("remote.windows.workspace.paneUnavailable")
            } else {
                format!("{} (Ctrl+Shift+E)", locale.get("menu.showFiles"))
            },
        }
    });
    let in_active = sessions.iter().filter(|s| s.workspace_id.is_some() && s.workspace_id == state.active_workspace_id).count();
    let running = sessions.iter().filter(|s| is_counted(s.kind()) && s.status() == "running").count();
    let machine = match active.and_then(|w| w.remote.as_ref().map(|r| (w, r))) {
        Some((w, r)) => {
            let c = connection(w);
            locale.get_with(
                if c.is_some_and(|c| c.connected) { "remote.status.connected" } else { "remote.status.notConnected" },
                &[("host", &host_with_os(locale, r.host_name.as_deref().unwrap_or(""), c.and_then(|c| c.platform.as_deref())))],
            )
        }
        None => locale.get(match context.runtime {
            Runtime::Checking => "window.status.checkingRuntimeAndModels",
            Runtime::Unknown => "window.status.checkingRuntime",
            Runtime::Known => "window.status.thisMachine",
        }),
    };
    ShellModel {
        version: context.version.into(),
        dialog_open: context.dialog_open,
        dashboard,
        panes: sessions.iter().map(|s| pane_info(locale, s, attention(s.id()), now)).collect(),
        entry: badges(sessions.iter().copied(), &attention),
        workspaces,
        header,
        layout,
        error: context.error.clone().filter(|e| !e.is_empty()),
        resource: context.resource.clone().filter(|r| !r.is_empty()),
        status: Status {
            machine,
            remote: active.and_then(|w| w.remote.as_ref().map(|_| connection(w).is_some_and(|c| c.connected))),
            panes: locale.get_with("window.status.paneCount", &[("count", &in_active.to_string())]),
            running: locale.get_with("window.status.runningCount", &[("count", &running.to_string())]),
            update_help: locale.get("window.status.updateBadgeHelp"),
            setup: context.setup_badge,
            chips: context.usage.and_then(|u| u.chips.clone()),
            chips_title: locale.get("windows.accountUsage.title"),
            chips_tooltip: locale.get("windows.accountUsage.chipsTooltip"),
        },
        usage: context.usage.map(|u| u.details.clone()),
        setup_needed: context.setup_needed,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::json::Wire;
    use serde_json::json;

    fn at(text: &str) -> DateTimeOffset {
        time::parse(Some(text)).unwrap()
    }

    fn session(value: serde_json::Value) -> RunSession {
        RunSession::read(&value).unwrap()
    }

    #[test]
    fn tones_and_words() {
        let en = Locale::new("en");
        assert_eq!(Tone::of("running").name(), "run");
        assert_eq!(Tone::of("cancelled").name(), "stop");
        assert_eq!(en.get(Tone::of("whatever").word_key()), "Ready");
    }

    /// WorkDashboard.cs:69 `Since`: minutes truncated; under 1 is now, then minutes, hours, days.
    #[test]
    fn age_labels_follow_work_dashboard_since() {
        let en = Locale::new("en");
        let now = at("2026-11-13T12:00:00Z");
        assert_eq!(age_label(&en, at("2026-11-13T11:59:30Z"), now), "just now");
        assert_eq!(age_label(&en, at("2026-11-13T11:15:00Z"), now), "45 min ago");
        assert_eq!(age_label(&en, at("2026-11-10T11:00:00.1234567+00:00"), now), "3 days ago");
        assert_eq!(age_label(&en, at("2026-11-13T13:00:00Z"), now), "just now", "a future time clamps to 0");
    }

    /// WorkDashboard.cs:98: an error pane whose last activity failed names its tool, then its age.
    #[test]
    fn a_failed_tool_is_named_in_the_sidebar() {
        let en = Locale::new("en");
        let s = session(json!({
            "id": "s1", "workspaceId": "w1", "status": "error", "titleMode": "fixed", "title": "T",
            "logs": [{"id": "l1", "kind": "assistant", "text": "x", "timestamp": "2026-11-13T11:00:00Z",
                      "activity": {"id": "a1", "provider": "claude", "kind": "command", "toolName": "Bash", "summary": "npm test", "state": "error"}}]
        }));
        let info = pane_info(&en, &s, Attention::default(), at("2026-11-13T12:00:00Z"));
        assert_eq!(info.meta, "· Bash failed · 1 hr ago");
        assert_eq!(info.tone, "err");
        assert!(!info.settled);
        assert_eq!(info.agent_label.as_deref(), Some("Claude"));
        assert_eq!(info.dash_meta, "· Bash failed · 1 hr ago");
    }

    /// WorkDashboard.cs:90-95: a running pane shows its clock and its context share; MainWindow.WebShell.Shell.cs:360 its activity.
    #[test]
    fn a_running_pane_shows_its_clock_context_and_activity() {
        let en = Locale::new("en");
        let s = session(json!({
            "id": "s1", "workspaceId": "w1", "status": "running", "model": "opus",
            "runTiming": {"startedAt": "2026-11-13T11:00:00Z", "lastObservedAt": "2026-11-13T11:00:05Z"},
            "sessionUsage": {"provider": "claude", "source": "claude.stream-json", "tokenScope": "response", "contextUsedTokens": 41500, "contextWindowTokens": 100000, "updatedAt": "2026-11-13T11:00:00Z"},
            "logs": [{"id": "l1", "kind": "assistant", "text": "  first line\nsecond", "timestamp": "2026-11-13T11:00:00Z"}]
        }));
        let info = pane_info(&en, &s, Attention::default(), at("2026-11-13T12:01:05Z"));
        assert!(info.meta.starts_with("· 1:01:05 · "), "{}", info.meta);
        assert!(info.meta.ends_with(&en.get_with("phone.card.context", &[("percent", "42")])));
        assert_eq!(info.activity.as_deref(), Some("first line"));
        // The dashboard names the model as the composer does (ModelLabel.Selection over the fallback catalogue).
        assert!(info.dash_meta.starts_with("· Opus · 1:01:05"), "{}", info.dash_meta);
    }

    /// MainWindow.WebShell.Shell.cs:359-361 and MainWindow.Navigation.cs:157: waiting words, questions before permissions.
    #[test]
    fn attention_turns_a_pane_waiting() {
        let en = Locale::new("en");
        let s = session(json!({"id": "s1", "workspaceId": "w1", "status": "running"}));
        let info = pane_info(&en, &s, Attention { questions: 1, permissions: 2 }, at("2026-11-13T12:00:00Z"));
        assert_eq!(info.tone, "wait");
        assert_eq!(info.waiting, 3);
        assert_eq!(info.wait, Some(en.get_with("phone.card.questions", &[("count", "1")])));
        assert_eq!(info.dash_wait, info.wait);
        let counts = badges([&s].into_iter(), &|_| Attention { questions: 0, permissions: 2 });
        assert_eq!((counts.permissions, counts.running, counts.idle), (2, 0, 0));
    }

    /// WorkspaceGitInfo.cs:13: porcelain v2 headers, a detached head, a dirty tree.
    #[test]
    fn git_status_parses_as_workspace_git_info() {
        let info = WorkspaceGitInfo::parse(
            "# branch.oid 0123456789abcdef\n# branch.head main\n# branch.upstream origin/main\n# branch.ab +2 -1\n1 .M N... a\n",
        )
        .unwrap();
        assert_eq!(
            info,
            WorkspaceGitInfo {
                branch: "main".into(),
                revision: Some("0123456789abcdef".into()),
                is_dirty: true,
                ahead: Some(2),
                behind: Some(1)
            }
        );
        let detached = WorkspaceGitInfo::parse("# branch.oid 0123456789abcdef\n# branch.head (detached)\n").unwrap();
        assert_eq!(detached.label(), "HEAD · 0123456");
        assert!(!detached.is_dirty);
        assert!(WorkspaceGitInfo::parse("nothing").is_none());
        let en = Locale::new("en");
        let model = git_model(&en, Some(&info)).unwrap();
        assert_eq!(model.help, format!("Git · main · {}\n{}", en.get("git.dirty"), en.get("git.localUpstream")));
    }

    /// DelegationSidebar.cs:93: a child under its open parent, the children of a closed parent under its node.
    #[test]
    fn delegated_children_list_under_their_parents() {
        let en = Locale::new("en");
        let root = if cfg!(windows) { "C:\\\\work\\\\a" } else { "/work/a" };
        let state = AppSnapshot::read(
            &serde_json::from_str(&format!(
                r#"{{"version":1,"workspaces":[{{"id":"w1","path":"{root}"}}],"sessions":[{{"id":"p1","workspaceId":"w1"}},{{"id":"c1","workspaceId":"w1","parentSessionId":"p1"}},{{"id":"c3","workspaceId":"w1"}}]}}"#
            ))
            .unwrap(),
        )
        .unwrap();
        let row = |id: &str, parent: &str, state: ChildState| DelegationChildRow {
            id: id.into(),
            parent_session_id: parent.into(),
            state,
            task: Some(format!("task {id}")),
            parent_branch: "main".into(),
            parent_checkout: Some(root.replace("\\\\", "\\")),
            can_undo: false,
            held_notices: 2,
            started_by_human: false,
            agent: "claude".into(),
        };
        let children =
            vec![row("c1", "p1", ChildState::Running), row("c2", "gone", ChildState::Reported), row("c3", "gone", ChildState::Merged)];
        let items = sidebar_items(&en, &state, "w1", &children, &|_| Attention::default());
        let text = serde_json::to_value(&items).unwrap();
        assert_eq!(text[0], json!({"type": "pane", "id": "p1", "depth": 0}));
        assert_eq!(text[1]["type"], "pane");
        assert_eq!(text[1]["id"], "c1");
        assert_eq!(text[1]["depth"], 1);
        assert_eq!(text[1]["card"]["actions"].as_array().unwrap().len(), 2, "merge and discard");
        assert!(text[1]["card"].get("held").is_none(), "held notices show only under a closed parent");
        assert_eq!(text[2]["type"], "parentClosed");
        assert_eq!(text[2]["count"], 2);
        assert_eq!(text[3]["type"], "closedChild");
        assert_eq!(text[3]["card"]["held"], json!(en.get_with("delegation.card.heldNotices", &[("count", "2")])));
        assert_eq!(text[4]["id"], "c3");
        assert_eq!(text[4]["type"], "pane");
        assert_eq!(items.len(), 5);
    }

    /// WorkDashboard.cs:67: waiting first, then running, failed, done, stopped, idle; newest first within a tone.
    #[test]
    fn dashboard_rows_are_ordered_as_work_dashboard_orders_them() {
        let mk = |id: &str, status: &str, at: &str| session(json!({"id": id, "workspaceId": "w1", "status": status, "createdAt": at}));
        let a = mk("a", "completed", "2026-11-13T10:00:00Z");
        let b = mk("b", "running", "2026-11-13T09:00:00Z");
        let c = mk("c", "completed", "2026-11-13T11:00:00Z");
        let d = mk("d", "idle", "2026-11-13T12:00:00Z");
        let cards = ordered(vec![
            make_card(&a, Attention::default()),
            make_card(&b, Attention::default()),
            make_card(&c, Attention::default()),
            make_card(&d, Attention { questions: 1, permissions: 0 }),
        ]);
        let ids: Vec<&str> = cards.iter().map(|c| c.session.id()).collect();
        assert_eq!(ids, ["d", "b", "c", "a"]);
    }

    /// WorkDashboard.cs:109: the first line starts at the first non-space and stops at a line break.
    #[test]
    fn first_lines() {
        assert_eq!(first_line("\n\n  hello world  \nnext"), "hello world");
        assert_eq!(first_line(&"가".repeat(250)).chars().count(), 200);
        assert_eq!(first_line("a\r\nb"), "a");
    }
}
