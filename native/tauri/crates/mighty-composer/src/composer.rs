//! The composer state the page draws (`{kind:"composer", state}`, page `src/composer/types.ts` `ComposerState`): C# `PaneView.
//! WebComposerState` (MainWindow.WebShell.Composer.cs:560) with what it reads of the never-shown WinUI composer worked out by the same
//! rules here: `RefreshComposerState` (MainWindow.cs:592: the block reason, the placeholder, can-send), `RefreshQueuedComposer`
//! (MainWindow.QueuedComposer.cs:133: queue, worktree, menu only), `RunNowPane`/`RunNowRows` (:290, :305), `WebPills` (:433),
//! `WebHeader` (:545) and `HeaderFigures` (MainWindow.PaneHeader.cs:303). Field names and order are the C# anonymous object's;
//! a null is left out, as `Wire.Json` writes it.
//!
//! What later stages give it is typed in [`WindowRules`] and listed in [`Pending`].

use mighty_core::attachment::RunAttachment;
use mighty_core::locale::Locale;
use mighty_core::model::{RunSession, Workspace};
use mighty_core::model_label;
use mighty_core::normalize;
use mighty_core::shell::{self, Tone};
use mighty_core::time::DateTimeOffset;
use serde::Serialize;

use crate::ask_targets::{self, AskTarget};
use crate::attachments;
use crate::presentation::plan as plan_support;
use crate::queue::{self, ComposerSecondSend, QueueRunNowPath, QueuedComposerInput, RunNowPane};
use crate::runtime::{self, ModelCatalog, ProviderCapabilities, ProviderRuntime};

/// The composer's inputs later stages provide (each names the C# member it stands for). Until then the host passes the resting
/// value, which [`WindowRules::default`] holds.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Pending {
    /// `PaneRuntime(provider)` and `ProviderCatalog.Efforts` (run engine, W2): no runtime reads "checking the runtime".
    Runtime,
    /// `PendingRequests` (tool permissions and questions, W2).
    Attention,
    /// `BackgroundUpdateHolds`, `ManualMutationBlockReason`, `ManualUpdateReason` (CLI updates and plugin changes, W6).
    CliMaintenance,
    /// `SetupItemForMissingCli` (setup catalogue, W6).
    Setup,
    /// `service.SecondSend`, `WorktreeMenuOnly`, `HeldItems`, `Delegation.EndingRuns` (delegation, W7).
    Delegation,
    /// `BackgroundNoticeRuns.AwaitsNotice` (graph tracker, W3).
    AwaitsNotice,
    /// `RemoteWorkspaceFeatures.BlockedReason` for a remote workspace's pane (W8).
    RemoteBlock,
    /// `RefreshStyles`/`WebGuided`: the style row and the guide panel (style engine, frozen, W9).
    Style,
}

pub const PENDING: [Pending; 8] = [
    Pending::Runtime,
    Pending::Attention,
    Pending::CliMaintenance,
    Pending::Setup,
    Pending::Delegation,
    Pending::AwaitsNotice,
    Pending::RemoteBlock,
    Pending::Style,
];

/// An item delegation holds for the pane (C# `DelegationDeliveryItem` as the composer shows it).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HeldItem {
    pub id: String,
    pub text: String,
}

/// The never-shown `PaneView`'s own state: the editor's draft and attachments, its flags and its queue.
#[derive(Debug, Clone, Default)]
pub struct PaneViewState {
    pub draft: String,
    pub attachments: Vec<RunAttachment>,
    /// `attachmentErrorText.Text` (empty: none).
    pub attachment_error: Option<String>,
    pub starting: bool,
    pub queue_starting: bool,
    pub stopping: bool,
    pub checking_models: bool,
    pub attachments_loading: bool,
    pub queue: Vec<QueuedComposerInput>,
    /// `LaunchesInPlanMode`: the active style starts every request in plan mode.
    pub launches_in_plan_mode: bool,
}

/// The style row over the editor (`styleHost`), from the style engine.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StyleRow {
    pub label: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub badge: Option<String>,
    pub hint: String,
    pub enabled: bool,
}

/// What the window knows about the pane that the composer reads (C# `MainWindow` members). See [`Pending`].
#[derive(Debug, Clone)]
pub struct WindowRules {
    pub background_update_holds: bool,
    pub manual_mutation_block: Option<String>,
    pub manual_update_reason: Option<String>,
    pub setup_item_for_missing_cli: bool,
    pub remote_block: Option<String>,
    pub second_send: ComposerSecondSend,
    pub worktree_menu_only: bool,
    pub held_items: Vec<HeldItem>,
    pub ending_run: bool,
    pub pending_requests: usize,
    pub awaits_notice: bool,
    /// `AskTargetsFor(pane)` ([`ask_targets_for`]).
    pub ask_targets: Vec<AskTarget>,
    /// `askFallbackNotices[id]`.
    pub ask_notice: Option<String>,
    /// `Snapshot.StatusLineEnabled`.
    pub status_line_enabled: bool,
    pub style: Option<StyleRow>,
    pub guided: Option<serde_json::Value>,
}

impl Default for WindowRules {
    fn default() -> Self {
        WindowRules {
            background_update_holds: false,
            manual_mutation_block: None,
            manual_update_reason: None,
            setup_item_for_missing_cli: false,
            remote_block: None,
            second_send: ComposerSecondSend::Queue,
            worktree_menu_only: false,
            held_items: vec![],
            ending_run: false,
            pending_requests: 0,
            awaits_notice: false,
            ask_targets: vec![],
            ask_notice: None,
            status_line_enabled: false,
            style: None,
            guided: None,
        }
    }
}

/// Everything `composer_state` reads.
pub struct ComposerInputs<'a> {
    pub locale: Locale,
    pub now: DateTimeOffset,
    pub pane: &'a RunSession,
    pub workspace: Option<&'a Workspace>,
    /// `PaneRuntime(pane.Provider)`: None until the CLI was read.
    pub runtime: Option<&'a ProviderRuntime>,
    /// `ProviderCatalog.Efforts(provider, model, catalog, registeredModels)`: the levels the pane's model takes.
    pub efforts: &'a [String],
    pub view: &'a PaneViewState,
    pub window: &'a WindowRules,
}

impl ComposerInputs<'_> {
    fn catalog(&self) -> ModelCatalog {
        self.runtime.map(|r| r.catalog.clone()).unwrap_or_else(|| ModelCatalog::fallback(self.pane.provider(), &self.locale))
    }
    fn capabilities(&self) -> ProviderCapabilities {
        self.runtime.map(|r| r.capabilities.clone()).unwrap_or_else(|| ProviderCapabilities::fallback(self.pane.provider()))
    }
    fn remote(&self) -> bool {
        self.workspace.is_some_and(|w| w.remote.is_some())
    }
    fn settings(&self) -> mighty_core::model::RunSettings {
        self.pane.settings.clone().unwrap_or_default()
    }
}

// ── the page's model ────────────────────────────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct QueueHeld {
    pub id: String,
    pub text: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub run_now: Option<&'static str>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct QueueItem {
    pub id: String,
    pub text: String,
    pub attachments: Vec<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub run_now: Option<&'static str>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct QueueModel {
    pub held: Vec<QueueHeld>,
    pub items: Vec<QueueItem>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub waits_on_background: Option<&'static str>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AttachmentChip {
    pub id: String,
    pub name: String,
    pub media_type: String,
    pub size: usize,
}

/// A double as `Wire.Json` writes one: a whole number without a fraction.
pub(crate) fn dotnet_number<S: serde::Serializer>(value: &Option<f64>, serializer: S) -> Result<S::Ok, S::Error> {
    match value {
        Some(v) if v.fract() == 0.0 && v.abs() < 9e15 => serializer.serialize_i64(*v as i64),
        Some(v) => serializer.serialize_f64(*v),
        None => serializer.serialize_none(),
    }
}

/// `WebPills` (MainWindow.WebShell.Composer.cs:433), page `src/composer/menus.ts` `Pills`.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Pills {
    pub provider: String,
    pub model: String,
    pub model_tip: String,
    pub shows_effort: bool,
    pub effort: String,
    pub effort_enabled: bool,
    pub effort_tip: String,
    pub permission: String,
    pub permission_help: String,
    pub unlocked: bool,
    pub shows_fast: bool,
    pub fast: bool,
    pub fast_enabled: bool,
    pub more_on: bool,
    #[serde(skip_serializing_if = "Option::is_none", serialize_with = "dotnet_number")]
    pub context_percent: Option<f64>,
    pub resuming: bool,
}

/// `WebHeader` (MainWindow.WebShell.Composer.cs:545).
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct Header {
    pub word: String,
    pub tone: &'static str,
    pub timed: bool,
    pub rest: String,
    pub help: String,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Toggles {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub status_line: Option<bool>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct AskTargetOption {
    pub id: &'static str,
    pub label: &'static str,
}

/// `PaneView.WebComposerState` (MainWindow.WebShell.Composer.cs:560).
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ComposerState {
    pub pane_id: String,
    pub kind: String,
    pub busy: bool,
    pub starting: bool,
    pub stopping: bool,
    pub can_send: bool,
    pub checking_models: bool,
    pub attachments_loading: bool,
    pub placeholder_key: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub blocked: Option<String>,
    pub install: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub hold: Option<String>,
    pub primary: &'static str,
    pub primary_enabled: bool,
    pub worktree: bool,
    pub worktree_menu_only: bool,
    pub worktree_enabled: bool,
    pub queue: QueueModel,
    pub attachments: Vec<AttachmentChip>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub attachment_error: Option<String>,
    pub attachments_allowed: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pills: Option<Pills>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub style: Option<StyleRow>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub guided: Option<serde_json::Value>,
    pub header: Header,
    pub resumed: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub toggles: Option<Toggles>,
    pub ask_targets: Vec<AskTargetOption>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ask_target: Option<&'static str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ask_notice: Option<String>,
}

// ── the rules ───────────────────────────────────────────────────────────────────────────────────────────────────────────

fn blank(text: &str) -> bool {
    text.chars().all(char::is_whitespace)
}

/// `MainWindow.AskTargetsFor` (MainWindow.AskTargets.cs:68): a local Claude agent pane offers the installed targets once its
/// catalogue found oh-my-claudecode's ask. `offers_ask`: `SlashCatalogCache.OffersAsk` for the pane's provider and workspace.
pub fn ask_targets_for(
    pane: &RunSession,
    remote: bool,
    installed: &std::collections::HashSet<String>,
    offers_ask: Option<bool>,
) -> Vec<AskTarget> {
    if installed.is_empty() || !(pane.kind() == "claude" && pane.provider() == "claude" && !remote) {
        return vec![];
    }
    let slash: std::collections::HashSet<String> =
        if offers_ask == Some(true) { [ask_targets::SKILL.to_string()].into() } else { Default::default() };
    ask_targets::available(true, &slash, installed)
}

/// The pane's busy rule (`Session.Status == "running" || starting || queueStarting || BackgroundUpdateHolds`).
pub fn busy(i: &ComposerInputs) -> bool {
    i.pane.status() == "running" || i.view.starting || i.view.queue_starting || i.window.background_update_holds
}

/// What `RefreshComposerState` (MainWindow.cs:592) leaves: the block reason, the placeholder's key and whether Send may go.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Refreshed {
    pub reason: String,
    pub placeholder_key: &'static str,
    pub can_send: bool,
}

/// C# `RefreshComposerState` (MainWindow.cs:592-626).
pub fn refresh(i: &ComposerInputs) -> Refreshed {
    let l = &i.locale;
    let pane = i.pane;
    let settings = i.settings();
    let busy = busy(i);
    let caps = i.capabilities();
    let unsupported_effort = pane.kind() == "claude" && settings.effort != "default" && !i.efforts.contains(&settings.effort);
    let mut unsupported_settings = None;
    if pane.kind() == "claude"
        && settings.permission_mode == "auto"
        && i.runtime.map(|r| r.capabilities.permission_modes.iter().any(|m| m == "auto")) != Some(true)
    {
        unsupported_settings = Some(l.get("composer.hint.autoModeUnverified"));
    }
    if !i.view.attachments.is_empty() && !caps.attachments {
        unsupported_settings = Some(l.get("composer.hint.attachmentsUnsupported"));
    }
    let mutation_block = i.window.manual_mutation_block.clone();
    let remote_block = if i.remote() { i.window.remote_block.clone() } else { None };
    let available = i.runtime.map(|r| r.available) == Some(true);
    let reason = match &mutation_block {
        Some(block) => block.clone(),
        None if busy || i.view.attachments_loading => String::new(),
        None => match &remote_block {
            Some(block) => block.clone(),
            None if pane.kind() == "claude" && !available => {
                let detail = i.runtime.map_or_else(|| l.get("composer.hint.refreshRuntime"), |r| r.detail.clone());
                l.get_with("composer.hint.draftStillAllowed", &[("reason", &detail)])
            }
            None if unsupported_effort => l.get_with("composer.hint.effortUnverified", &[("effort", &settings.effort)]),
            None => unsupported_settings.clone().unwrap_or_default(),
        },
    };
    let asking = ask_targets::effective(pane.ask_target.as_deref(), &i.window.ask_targets).filter(|_| pane.ask_target.is_some());
    let placeholder_key = if i.view.checking_models {
        "composer.placeholder.checkingModels"
    } else if busy {
        if pane.kind() == "claude" && i.window.second_send == ComposerSecondSend::Worktree {
            "composer.placeholder.busyWorktree"
        } else {
            "composer.placeholder.busyQueue"
        }
    } else if pane.kind() == "shell" {
        "composer.placeholder.shell"
    } else if asking.is_some() {
        "composer.ask.placeholder"
    } else {
        "composer.placeholder.idle"
    };
    let can_send = mutation_block.is_none()
        && remote_block.is_none()
        && !busy
        && !i.view.attachments_loading
        && (pane.kind() == "shell" || available)
        && !unsupported_effort
        && unsupported_settings.is_none()
        && (!blank(&i.view.draft) || !i.view.attachments.is_empty() && asking.is_none());
    Refreshed { reason, placeholder_key, can_send }
}

/// C# `RunNowPane` (MainWindow.QueuedComposer.cs:290): the pane as 바로 실행 reads it.
pub fn run_now_pane(i: &ComposerInputs) -> RunNowPane {
    let pane = i.pane;
    let second = i.window.second_send;
    let holds = i.window.background_update_holds || i.window.manual_update_reason.is_some();
    let recording = i.window.ending_run;
    RunNowPane {
        busy: pane.status() == "running" || i.view.starting || i.view.queue_starting || holds || recording,
        held_for_update: holds,
        second_send: second,
        menu_only: second == ComposerSecondSend::Queue && i.window.worktree_menu_only,
        waits_on_background_only: pane.kind() == "claude"
            && pane.provider() == "claude"
            && !i.remote()
            && !i.view.stopping
            && !recording
            && queue::waiting_on_background(pane.background_work.as_ref()),
        held: i.window.held_items.len(),
        notice_turn_running: queue::notice_turn_running(pane.graph_runs()),
    }
}

/// C# `RunNowRows` (MainWindow.QueuedComposer.cs:305): what 바로 실행 does on each row, held first, the first id kept.
pub fn run_now_rows(i: &ComposerInputs) -> Vec<(String, QueueRunNowPath)> {
    let pane = run_now_pane(i);
    let mut rows: Vec<(String, QueueRunNowPath)> = Vec::new();
    let mut add = |id: &str, path: QueueRunNowPath| {
        if !rows.iter().any(|(r, _)| r == id) {
            rows.push((id.to_string(), path));
        }
    };
    for (index, item) in i.window.held_items.iter().enumerate() {
        add(&item.id, queue::decide(Some(index), "", 0, &pane, false));
    }
    for item in &i.view.queue {
        add(&item.id, queue::decide(None, &item.text, item.attachments.len(), &pane, queue::is_continuation(item)));
    }
    rows
}

fn run_now_name(rows: &[(String, QueueRunNowPath)], id: &str) -> Option<&'static str> {
    let path = rows.iter().find(|(r, _)| r == id).map_or(QueueRunNowPath::Hidden, |(_, p)| *p);
    (path != QueueRunNowPath::Hidden).then(|| path.name())
}

/// `SessionUsage.ContextPercent`: used over window, clamped to 0-100.
pub fn context_percent(usage: &mighty_core::model::SessionUsage) -> Option<f64> {
    match (usage.context_used_tokens, usage.context_window_tokens) {
        (Some(used), Some(window)) if used >= 0 && window > 0 => Some((used as f64 / window as f64 * 100.0).clamp(0.0, 100.0)),
        _ => None,
    }
}

fn pane_usage(pane: &RunSession) -> Option<&mighty_core::model::SessionUsage> {
    pane.session_usage.as_ref().filter(|u| u.provider == pane.provider())
}

/// `WebPills` (MainWindow.WebShell.Composer.cs:433).
pub fn pills(i: &ComposerInputs) -> Pills {
    let l = &i.locale;
    let pane = i.pane;
    let settings = i.settings();
    let catalog = i.catalog();
    let caps = i.capabilities();
    let levels = i.efforts;
    let selection = model_label::selection(pane, &catalog.options(), l);
    let description = catalog.find(pane.model()).map(|m| m.description.as_str()).filter(|d| !d.is_empty());
    let usage = pane_usage(pane);
    Pills {
        provider: pane.provider().into(),
        model_tip: format!(
            "{} · {selection}{}",
            runtime::beta_label(pane.provider(), runtime::provider_label(pane.provider()), l),
            description.map(|d| format!("\n{d}")).unwrap_or_default()
        ),
        model: selection,
        shows_effort: caps.effort || settings.effort != "default",
        effort: runtime::effort_pill_label(&settings.effort, l),
        effort_enabled: !levels.is_empty() || settings.effort != "default",
        effort_tip: if levels.is_empty() {
            l.get("composer.effort.unknownLevels")
        } else {
            format!("{} · {}", l.get("composer.effort.label"), l.get("settings.run.appliesNextRequest"))
        },
        permission: runtime::permission_label(pane.provider(), &settings.permission_mode, l),
        permission_help: runtime::permission_help(pane.provider(), &settings.permission_mode, l),
        unlocked: settings.permission_mode == "fullAccess",
        shows_fast: pane.provider() == "codex" && (caps.fast_mode || settings.fast_mode),
        fast: settings.fast_mode,
        fast_enabled: caps.fast_mode || settings.fast_mode,
        more_on: settings.web_search != "default"
            || settings.network_access
            || settings.max_turns.is_some()
            || settings.max_budget_usd.is_some(),
        context_percent: usage.and_then(context_percent).filter(|p| p.is_finite()).map(f64::round_ties_even),
        resuming: pane.resume_id.is_some(),
    }
}

/// C# `HeaderFigures` (MainWindow.PaneHeader.cs:303): the figures after the clock, every value with its name, and the tooltip.
pub fn header_figures(i: &ComposerInputs) -> (String, String, String) {
    let l = &i.locale;
    let pane = i.pane;
    let named_figure = |key: &str, value: &str| l.get_with("pane.hero.figure", &[("label", &l.get(key)), ("value", value)]);
    let mut shown = Vec::new();
    let mut named = Vec::new();
    let timing = pane.run_timing.as_ref();
    let timed = pane.kind() != "shell" && timing.is_some();
    if let Some(timing) = timing.filter(|_| timed) {
        named.push(named_figure("phone.session.hero.elapsed", &shell::timing_label(l, timing, i.now)));
    }
    let usage = pane_usage(pane);
    if let Some(percent) = usage.and_then(context_percent).filter(|p| p.is_finite()) {
        // Math.Round(…, MidpointRounding.AwayFromZero)
        let value = format!("{}%", percent.clamp(0.0, 100.0).round());
        shown.push(value.clone());
        named.push(named_figure("phone.session.hero.context", &value));
    }
    if let Some(cost) = usage.and_then(|u| u.cost_usd).filter(|c| c.is_finite() && *c >= 0.0) {
        let value = format!("${}", fixed2(cost));
        shown.push(value.clone());
        named.push(named_figure("phone.session.hero.cost", &value));
    }
    let logs = pane.logs();
    if let Some(request) = logs.iter().rposition(|e| e.kind() == "user") {
        let tools = logs[request + 1..].iter().filter(|e| e.activity.as_ref().is_some_and(|a| a.kind != "turn")).count();
        let value = named_figure("phone.session.hero.tools", &tools.to_string());
        shown.push(value.clone());
        named.push(value);
    }
    let mut rest = shown.join(" · ");
    if !rest.is_empty() && timed {
        rest = format!(" · {rest}");
    }
    named.push(normalize::provider_name(pane.provider()).into());
    let catalog = i.catalog();
    let model = pane.model().trim();
    let model_label = if !model.is_empty() && model != "default" {
        Some(model_label::selection(pane, &catalog.options(), l))
    } else {
        model_label::reported_model(pane).map(|r| model_label::text(r, None, None))
    };
    if let Some(label) = model_label {
        named.push(label);
    }
    let full = named.join(" · ");
    let help = match timing.filter(|_| timed) {
        None => full.clone(),
        Some(t) => format!(
            "{full}\n{}",
            l.get(if t.is_approximate {
                "pane.hero.elapsed.approximate"
            } else if t.finished_at.is_none() {
                "pane.hero.elapsed.running"
            } else {
                "pane.hero.elapsed.finished"
            })
        ),
    };
    (rest, full, help)
}

/// `WebHeader` (MainWindow.WebShell.Composer.cs:545): the pane header's word, in its tone, and the figures after the clock.
pub fn header(i: &ComposerInputs) -> Header {
    let l = &i.locale;
    let pane = i.pane;
    let pending = i.window.pending_requests;
    let awaiting = pending == 0 && i.window.awaits_notice;
    let shown = if awaiting { "waiting" } else { shell::display_status(pane.status(), pending) };
    let word = if pending > 0 {
        l.get_with("phone.card.attention", &[("count", &pending.to_string())])
    } else if awaiting {
        l.get("graph.state.awaitingNotice")
    } else if let Some(waiting) = plan_support::background_status(pane.background_work.as_ref(), l).filter(|_| pane.status() == "running") {
        waiting
    } else {
        runtime::state_label(shown, l)
    };
    let (rest, _, help) = header_figures(i);
    Header { word, tone: Tone::of(shown).name(), timed: pane.kind() != "shell" && pane.run_timing.is_some(), rest, help }
}

/// C# `PaneView.WebComposerState` (MainWindow.WebShell.Composer.cs:560).
pub fn composer_state(i: &ComposerInputs) -> ComposerState {
    let pane = i.pane;
    let view = i.view;
    let refreshed = refresh(i);
    let busy = busy(i);
    let has_content = !blank(&view.draft) || !view.attachments.is_empty();
    let queueable = busy && !view.starting && !view.queue_starting && pane.kind() == "claude" && has_content;
    let primary = if queueable {
        "queue"
    } else if busy {
        "stop"
    } else {
        "send"
    };
    // RefreshQueuedComposer (MainWindow.QueuedComposer.cs:138-144).
    let second = if queueable { i.window.second_send } else { ComposerSecondSend::Queue };
    let worktree_menu_only = queueable && second == ComposerSecondSend::Queue && i.window.worktree_menu_only;
    let offers_worktree = (second == ComposerSecondSend::Worktree || worktree_menu_only) && !blank(&view.draft);
    let rows = run_now_rows(i);
    let mutation_block = i.window.manual_mutation_block.as_ref();
    let queue_enabled = !view.starting
        && !view.queue_starting
        && !view.attachments_loading
        && !view.stopping
        && view.queue.len() < queue::MAXIMUM_ITEMS
        && mutation_block.is_none();
    let reason = refreshed.reason;
    let remote = i.remote();
    ComposerState {
        pane_id: pane.id().into(),
        kind: pane.kind().into(),
        busy,
        starting: view.starting || view.queue_starting,
        stopping: view.stopping,
        can_send: refreshed.can_send,
        checking_models: view.checking_models,
        attachments_loading: view.attachments_loading,
        placeholder_key: refreshed.placeholder_key,
        install: !reason.is_empty() && mutation_block.is_none() && i.window.setup_item_for_missing_cli,
        blocked: (!reason.is_empty()).then_some(reason),
        hold: i.window.background_update_holds.then(|| runtime::provider_label(pane.provider()).to_string()),
        primary,
        primary_enabled: if primary == "queue" {
            queue_enabled
        } else if busy {
            !view.stopping
        } else {
            refreshed.can_send
        },
        worktree: offers_worktree,
        worktree_menu_only,
        worktree_enabled: offers_worktree && queue_enabled,
        queue: QueueModel {
            held: i
                .window
                .held_items
                .iter()
                .map(|h| QueueHeld { id: h.id.clone(), text: h.text.clone(), run_now: run_now_name(&rows, &h.id) })
                .collect(),
            items: view
                .queue
                .iter()
                .map(|q| QueueItem {
                    id: q.id.clone(),
                    text: q.text.clone(),
                    attachments: q.attachments.iter().map(|a| a.name.clone()).collect(),
                    run_now: run_now_name(&rows, &q.id),
                })
                .collect(),
            waits_on_background: queue::background::waits_on_background(pane.background_work.as_ref(), view.queue.len()).then_some(
                if i.window.second_send == ComposerSecondSend::Worktree {
                    "queue.waitingOnBackground.windowsWorktree"
                } else {
                    "queue.waitingOnBackground.windows"
                },
            ),
        },
        attachments: view
            .attachments
            .iter()
            .map(|a| AttachmentChip {
                id: a.id.clone(),
                name: a.name.clone(),
                media_type: a.media_type.clone(),
                size: attachments::decoded_length(a),
            })
            .collect(),
        attachment_error: view.attachment_error.clone().filter(|e| !e.is_empty()),
        attachments_allowed: pane.kind() != "shell",
        pills: (pane.kind() != "shell").then(|| pills(i)),
        style: i.window.style.clone(),
        guided: i.window.guided.clone(),
        header: header(i),
        resumed: pane.resume_id.is_some(),
        toggles: (pane.kind() == "claude" && pane.provider() == "claude")
            .then(|| Toggles { status_line: (!remote).then_some(i.window.status_line_enabled) }),
        ask_targets: i.window.ask_targets.iter().map(|t| AskTargetOption { id: t.id, label: t.label }).collect(),
        ask_target: ask_targets::effective(pane.ask_target.as_deref(), &i.window.ask_targets).map(|t| t.id),
        ask_notice: i.window.ask_notice.clone(),
    }
}

/// `value.ToString("0.00", InvariantCulture)`: two places, a half rounded away from zero on the shortest decimal form.
pub(crate) fn fixed2(value: f64) -> String {
    let text = format!("{value}");
    let (whole, fraction) = text.split_once('.').unwrap_or((&text, ""));
    let digits: Vec<u8> = fraction.bytes().map(|b| b - b'0').collect();
    let mut cents: i64 = whole.trim_start_matches('-').parse::<i64>().unwrap_or(0) * 100
        + digits.first().copied().unwrap_or(0) as i64 * 10
        + digits.get(1).copied().unwrap_or(0) as i64;
    if digits.get(2).copied().unwrap_or(0) >= 5 {
        cents += 1;
    }
    let sign = if value < 0.0 && cents > 0 { "-" } else { "" };
    format!("{sign}{}.{:02}", cents / 100, cents % 100)
}
