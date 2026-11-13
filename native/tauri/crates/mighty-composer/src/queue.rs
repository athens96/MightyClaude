//! The queue behind a busy pane: the second send (C# `ComposerSecondSends`, Delegation.cs:68-102, with the
//! `DelegationProviders` rules it reads, DelegationProviders.cs:36-60), 바로 실행 (`QueueRunNow.cs`), the buffer and its notices
//! (`QueuedInputBuffer.cs`, `RunBlock`), and how new input meets background work (`BackgroundQueuePolicy.cs`).

use mighty_core::attachment::RunAttachment;
use mighty_core::graph::{self, MightyGraphRun};
use mighty_core::locale::Locale;
use mighty_core::model::{BackgroundWork, RunSession};
use mighty_core::wire;

use crate::attachments;

// ── the second send ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `ComposerSecondSend` (Delegation.cs:68): what Ctrl+Enter does with the text while the pane is busy.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ComposerSecondSend {
    /// A new child pane of the same agent in its own worktree runs the text now.
    Worktree,
    /// The text waits for the next request, as Enter does.
    Queue,
}

/// C# `DelegationProviders.IsAgentPane`: an agent pane (kind claude) running Claude, Codex or Antigravity.
pub fn is_agent_pane(kind: &str, provider: &str) -> bool {
    kind == "claude" && matches!(provider, "claude" | "codex" | "gemini")
}

/// C# `DelegationProviders.ChildCanWrite`.
pub fn child_can_write(provider: &str, mode: &str) -> bool {
    match provider {
        "claude" => true,
        "codex" => matches!(mode, "acceptEdits" | "onRequest" | "fullAccess"),
        "gemini" => matches!(mode, "acceptEdits" | "fullAccess"),
        _ => false,
    }
}

/// C# `DelegationProviders.CanParentByHuman`.
pub fn can_parent_by_human(kind: &str, provider: &str, mode: &str) -> bool {
    is_agent_pane(kind, provider) && child_can_write(provider, mode)
}

/// C# `ComposerSecondSends.Choose` (Delegation.cs:85).
pub fn choose_second_send(
    kind: &str,
    provider: &str,
    mode: &str,
    is_child: bool,
    delegation_on: bool,
    can_host_worktree: bool,
) -> ComposerSecondSend {
    if delegation_on && !is_child && can_host_worktree && can_parent_by_human(kind, provider, mode) {
        ComposerSecondSend::Worktree
    } else {
        ComposerSecondSend::Queue
    }
}

/// C# `ComposerSecondSends.ReturnsToComposer` (Delegation.cs:92).
pub fn returns_to_composer(child_agent: Option<&str>, parent_agent: &str) -> bool {
    child_agent.is_some_and(|c| c != parent_agent)
}

/// C# `ComposerSecondSends.MenuOnly` (Delegation.cs:99). `any_choice_unblocked`: one of the worktree menu's choices
/// (`DelegationChildChoice`) can run now.
pub fn menu_only(
    kind: &str,
    provider: &str,
    mode: &str,
    is_child: bool,
    delegation_on: bool,
    can_host_worktree: bool,
    any_choice_unblocked: bool,
) -> bool {
    delegation_on
        && !is_child
        && can_host_worktree
        && is_agent_pane(kind, provider)
        && !can_parent_by_human(kind, provider, mode)
        && any_choice_unblocked
}

// ── 바로 실행 ───────────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `QueueRunNowPath` (QueueRunNow.cs:9).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum QueueRunNowPath {
    Worktree,
    AgentMenu,
    Here,
    Steer,
    Held,
    WaitsForTurn,
    WaitsForHeld,
    WaitsForUpdate,
    Hidden,
}

impl QueueRunNowPath {
    pub const ALL: [QueueRunNowPath; 9] = [
        QueueRunNowPath::Worktree,
        QueueRunNowPath::AgentMenu,
        QueueRunNowPath::Here,
        QueueRunNowPath::Steer,
        QueueRunNowPath::Held,
        QueueRunNowPath::WaitsForTurn,
        QueueRunNowPath::WaitsForHeld,
        QueueRunNowPath::WaitsForUpdate,
        QueueRunNowPath::Hidden,
    ];

    /// C# `QueueRunNow.Name`: the fixture's and the page's word (`worktree`, `agentMenu`, `waitsForTurn`, …).
    pub fn name(self) -> &'static str {
        match self {
            QueueRunNowPath::Worktree => "worktree",
            QueueRunNowPath::AgentMenu => "agentMenu",
            QueueRunNowPath::Here => "here",
            QueueRunNowPath::Steer => "steer",
            QueueRunNowPath::Held => "held",
            QueueRunNowPath::WaitsForTurn => "waitsForTurn",
            QueueRunNowPath::WaitsForHeld => "waitsForHeld",
            QueueRunNowPath::WaitsForUpdate => "waitsForUpdate",
            QueueRunNowPath::Hidden => "hidden",
        }
    }

    /// C# `QueueRunNow.Runs`: whether the button does something.
    pub fn runs(self) -> bool {
        matches!(
            self,
            QueueRunNowPath::Worktree | QueueRunNowPath::AgentMenu | QueueRunNowPath::Here | QueueRunNowPath::Steer | QueueRunNowPath::Held
        )
    }

    /// C# `QueueRunNow.Wire`: the phone's `runNow` and `runNowReason`; None draws no button.
    pub fn wire(self) -> Option<(&'static str, Option<&'static str>)> {
        match self {
            QueueRunNowPath::Worktree => Some(("worktree", None)),
            QueueRunNowPath::Here | QueueRunNowPath::Steer | QueueRunNowPath::Held => Some(("here", None)),
            QueueRunNowPath::WaitsForTurn => Some(("unavailable", Some("turn"))),
            QueueRunNowPath::WaitsForHeld => Some(("unavailable", Some("held"))),
            QueueRunNowPath::WaitsForUpdate => Some(("unavailable", Some("update"))),
            QueueRunNowPath::AgentMenu => Some(("unavailable", Some("agent"))),
            QueueRunNowPath::Hidden => None,
        }
    }
}

/// C# `QueueRunNow.Pane` (QueueRunNow.cs:41): the pane as 바로 실행 reads it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RunNowPane {
    pub busy: bool,
    pub held_for_update: bool,
    pub second_send: ComposerSecondSend,
    pub menu_only: bool,
    pub waits_on_background_only: bool,
    pub held: usize,
    pub notice_turn_running: bool,
}

/// C# `QueueRunNow.Decide` (QueueRunNow.cs:52).
pub fn decide(held_index: Option<usize>, text: &str, attachments: usize, pane: &RunNowPane, continuation: bool) -> QueueRunNowPath {
    if let Some(index) = held_index {
        return if index != 0 {
            QueueRunNowPath::Hidden
        } else if pane.busy && pane.notice_turn_running {
            QueueRunNowPath::WaitsForTurn
        } else {
            QueueRunNowPath::Held
        };
    }
    if !pane.busy && !pane.held_for_update {
        return QueueRunNowPath::Here;
    }
    if pane.held_for_update {
        return QueueRunNowPath::WaitsForUpdate;
    }
    if continuation {
        return QueueRunNowPath::WaitsForTurn;
    }
    // A new worktree needs written words for its task; files alone wait.
    let words = !text.chars().all(char::is_whitespace);
    if words && pane.second_send == ComposerSecondSend::Worktree {
        return QueueRunNowPath::Worktree;
    }
    if words && pane.menu_only {
        return QueueRunNowPath::AgentMenu;
    }
    if pane.waits_on_background_only && !pane.notice_turn_running && attachments == 0 {
        return if pane.held == 0 { QueueRunNowPath::Steer } else { QueueRunNowPath::WaitsForHeld };
    }
    QueueRunNowPath::WaitsForTurn
}

/// C# `QueueRunNow.IsContinuation`: the follow-up of an allowed Antigravity card.
pub fn is_continuation(item: &QueuedComposerInput) -> bool {
    item.continuation
}

// ── the buffer ──────────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `QueuedComposerInput` (QueuedInputBuffer.cs:7). The Antigravity grant and card the C# row may carry belong to the run engine
/// (W5); here `continuation` says the row carries one, which is all `QueueRunNow.IsContinuation` asks.
#[derive(Debug, Clone, PartialEq)]
pub struct QueuedComposerInput {
    pub id: String,
    pub text: String,
    pub attachments: Vec<RunAttachment>,
    pub permission_mode_override: Option<String>,
    pub prepared: bool,
    pub continuation: bool,
}

/// C# `QueuedInputBuffer` (QueuedInputBuffer.cs:11): requests behind a running turn. Errors pause; an explicit stop cancels.
#[derive(Debug, Clone, Default)]
pub struct QueuedInputBuffer {
    items: Vec<QueuedComposerInput>,
}

/// C# `QueuedInputBuffer.MaximumItems`.
pub const MAXIMUM_ITEMS: usize = 16;

/// Why the buffer refused an item: `InvalidOperationException` (full) or `ArgumentException` (the text or its files).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum QueueError {
    Full(String),
    Invalid(String),
}

impl std::fmt::Display for QueueError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            QueueError::Full(m) | QueueError::Invalid(m) => f.write_str(m),
        }
    }
}

impl std::error::Error for QueueError {}

impl QueuedInputBuffer {
    pub fn new() -> Self {
        QueuedInputBuffer::default()
    }

    pub fn items(&self) -> &[QueuedComposerInput] {
        &self.items
    }

    /// C# `QueuedInputBuffer.Add` (QueuedInputBuffer.cs:16).
    pub fn add(
        &mut self,
        text: &str,
        files: Vec<RunAttachment>,
        permission_mode_override: Option<&str>,
        prepared: bool,
        continuation: bool,
        l: &Locale,
    ) -> Result<QueuedComposerInput, QueueError> {
        if self.items.len() >= MAXIMUM_ITEMS {
            return Err(QueueError::Full(l.get("queue.full")));
        }
        if (text.chars().all(char::is_whitespace) && files.is_empty()) || crate::text::utf16_len(text) > 100_000 || text.contains('\0') {
            return Err(QueueError::Invalid(l.get("wire.startRun.emptyInput")));
        }
        if !files.is_empty() {
            attachments::validate(&files, l).map_err(QueueError::Invalid)?;
        }
        let item = QueuedComposerInput {
            id: wire::new_id(),
            text: text.into(),
            attachments: files,
            permission_mode_override: permission_mode_override.map(str::to_string),
            prepared,
            continuation,
        };
        self.items.push(item.clone());
        Ok(item)
    }

    pub fn remove(&mut self, id: &str) -> bool {
        let before = self.items.len();
        self.items.retain(|item| item.id != id);
        self.items.len() != before
    }

    /// The queued request moved to the head (바로 실행 in this pane); false when it is not queued.
    pub fn move_to_front(&mut self, id: &str) -> bool {
        let Some(index) = self.items.iter().position(|item| item.id == id) else { return false };
        let item = self.items.remove(index);
        self.items.insert(0, item);
        true
    }

    /// An item back at `index` as it was queued, without the 16-item check, and never twice.
    pub fn insert(&mut self, index: usize, item: QueuedComposerInput) {
        if !self.items.iter().any(|queued| queued.id == item.id) {
            let at = index.min(self.items.len());
            self.items.insert(at, item);
        }
    }

    pub fn clear(&mut self) {
        self.items.clear();
    }

    /// C# `QueuedInputBuffer.Settle` (QueuedInputBuffer.cs:41): whether the queue drains now.
    pub fn settle(&mut self, status: &str, keep_on_stop: bool) -> bool {
        if status == "stopped" && !keep_on_stop {
            self.clear();
        }
        !self.items.is_empty() && (matches!(status, "completed" | "idle") || status == "stopped" && keep_on_stop)
    }
}

/// C# `QueuedInputBuffer.SettleNotice` (QueuedInputBuffer.cs:51): what a run's end leaves about what waited behind it.
pub fn settle_notice(status: &str, count: usize, keep_on_stop: bool, l: &Locale) -> Option<String> {
    if count == 0 {
        return None;
    }
    let n = count.to_string();
    if status == "stopped" && !keep_on_stop {
        return Some(l.get_with("queue.log.cancelledOnStop", &[("count", &n)]));
    }
    (status == "error").then(|| l.get_with("queue.log.heldAfterError", &[("count", &n)]))
}

/// C# `QueuedInputBuffer.UnrunnableNotice`.
pub fn unrunnable_notice(count: usize, reason: &str, l: &Locale) -> String {
    l.get_with("queue.log.cancelledUnrunnable", &[("count", &count.to_string()), ("reason", reason)])
}

/// C# `QueuedInputBuffer.KeptNotice`.
pub fn kept_notice(error: &str, l: &Locale) -> String {
    l.get_with("queue.log.keptAfterFailure", &[("error", error)])
}

/// What `RunBlock.Reason` reads of a pane's CLI (C# `ProviderRuntime`, Models.cs:511): given by the run engine (W2).
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct RuntimeReading {
    pub name: String,
    pub available: bool,
    pub detail: String,
    /// `Capabilities.PermissionModes`.
    pub permission_modes: Vec<String>,
}

/// C# `RunBlock.Reason` (QueuedInputBuffer.cs:68): why a pane cannot run for good, None while it may (or before its CLI was read).
pub fn run_block_reason(pane: &RunSession, runtime: Option<&RuntimeReading>, l: &Locale) -> Option<String> {
    if pane.kind() == "shell" {
        return None;
    }
    let runtime = runtime?;
    if !runtime.available {
        return Some(if runtime.detail.is_empty() {
            l.get_with("run.blocked.installCLI", &[("name", &runtime.name)])
        } else {
            runtime.detail.clone()
        });
    }
    let mode = pane.settings.as_ref().map_or("manual", |s| s.permission_mode.as_str());
    let has = |m: &str| runtime.permission_modes.iter().any(|x| x == m);
    if mode == "auto" && !has("auto") {
        return Some(l.get("composer.hint.autoModeUnverified"));
    }
    if mode == "onRequest" && !has("onRequest") {
        return Some(l.get("run.blocked.onRequestNeedsUpdate"));
    }
    None
}

// ── background work ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `BackgroundWork.WaitingOnBackground` (ClaudePlanMode.cs:378): the turn ended and a task still runs.
pub fn waiting_on_background(work: Option<&BackgroundWork>) -> bool {
    work.is_some_and(|w| w.turn_ended && w.tasks.as_deref().unwrap_or(&[]).iter().any(|t| t.status.as_deref() == Some("running")))
}

/// C# `BackgroundWork.Running`: the tasks still running.
pub fn running_tasks(work: &BackgroundWork) -> usize {
    work.tasks.as_deref().unwrap_or(&[]).iter().filter(|t| t.status.as_deref() == Some("running")).count()
}

/// C# `BackgroundQueuePolicy` (BackgroundQueuePolicy.cs:10).
pub mod background {
    use super::*;

    /// The queue's notice: what is queued starts only once the background work ends.
    pub fn waits_on_background(work: Option<&BackgroundWork>, queued: usize) -> bool {
        queued > 0 && waiting_on_background(work)
    }

    /// Run next steers the first queued request into the running Claude process.
    pub fn steers_run_next(
        work: Option<&BackgroundWork>,
        local_claude: bool,
        stopping: bool,
        queued: usize,
        first_has_attachments: bool,
    ) -> bool {
        queued > 0 && !first_has_attachments && local_claude && !stopping && waiting_on_background(work)
    }

    /// Stop on a run whose turn is already over keeps the queue.
    pub fn stop_keeps_queue(work: Option<&BackgroundWork>) -> bool {
        work.is_some_and(|w| w.turn_ended)
    }

    /// The permission mode a queued item records for its run.
    pub fn queued_override(launches_in_plan: bool) -> Option<&'static str> {
        launches_in_plan.then_some("plan")
    }
}

/// `MightyGraphSupport.NoticeTurnRunning` (MightyGraph.cs:270): a notice run after the user's newest request has not ended.
pub fn notice_turn_running(runs: &[MightyGraphRun]) -> bool {
    let notice = |run: &MightyGraphRun| run.notice_run == Some(true);
    let from = runs.iter().rposition(|run| !notice(run)).map_or(0, |n| n + 1);
    runs[from..].iter().any(|run| notice(run) && !graph::terminal(run.status.as_deref().unwrap_or("")))
}

/// `MightyGraphSupport.NoticeTurnEnded` (MightyGraph.cs:280).
pub fn notice_turn_ended(before: &[MightyGraphRun], after: &[MightyGraphRun]) -> bool {
    notice_turn_running(before) && !notice_turn_running(after)
}

/// `MightyGraphSupport.NewestRequestRun` (MightyGraph.cs:257): the user's newest request (not a notice run).
pub fn newest_request_run(runs: &[MightyGraphRun]) -> Option<&MightyGraphRun> {
    runs.iter().rev().find(|run| run.notice_run != Some(true))
}
