//! The never-shown pane view of each pane the page writes in (C# `PaneView`, made by `WebPane`, MainWindow.WebShell.Composer.cs:24):
//! its editor's draft and attachments, its flags, its queue, its plan card's draft and its Mighty view's own state. From it and the
//! state, the composer's model (`composer_state`, C# `WebComposerState`) and the cards (`cards`, C# `WebCards`) are worked out by
//! mighty-composer's rules; [`flush`] pushes each when its JSON changed (`composer` and `cards`, 30 ms, C# `FlushWebComposers`,
//! `FlushWebCards`), the cards again each second while an open background strip has a task running.
//!
//! What later stages give the composer and the cards stays at its resting value (`WindowRules::default`, no Antigravity card, no
//! terminal prompt, no web-open prompts, no sign-in card, no status line, no style): README "Pending inputs".

use std::sync::Arc;
use std::time::{Duration, Instant};

use mighty_composer::cards::{self, Card, CardInputs, PlanDraft};
use mighty_composer::composer::{self, ComposerInputs, ComposerState, PaneViewState, WindowRules};
use mighty_composer::queue::QueuedInputBuffer;
use mighty_core::model::{AppSnapshot, RunSession};
use mighty_core::time::DateTimeOffset;
use mighty_graph::view::MightyPane;
use mighty_run::permissions::ToolPermissionRequest;

use crate::host::{Host, View};

/// C# `input.MaxLength`: the editor holds at most 100,000 characters.
pub const DRAFT_LIMIT: usize = 100_000;

/// One pane's never-shown view.
#[derive(Default)]
pub struct PaneUi {
    /// The editor's draft, attachments, flags and (mirrored from `queue` before each read) the queue.
    pub view: PaneViewState,
    pub queue: QueuedInputBuffer,
    /// `composerSubmissionVersion`: a send or stop since a start began makes that start's answer moot.
    pub submission: u64,
    /// `queueKeptOnStop`: stopped while only background work ran, so the queue survives and runs next.
    pub queue_kept_on_stop: bool,
    /// The plan card's draft (`planDraft`): its answer being sent, or why it failed.
    pub plan_draft: Option<PlanDraft>,
    /// The menu a pill opened last (`webMenu`), so a choice runs the row the page showed.
    pub menu: Option<(String, Vec<mighty_composer::menus::MenuRow>)>,
    /// The Mighty view's own state (opened blocks, result files, timeline flips, record sizes).
    pub mighty: MightyPane,
    /// Something the composer or the cards read changed outside the pane's record (a request, a runtime read, a page method).
    pub dirty: bool,
    /// The pane's record the composer and cards were last made from.
    seen: Option<Arc<RunSession>>,
    composer_sent: Option<String>,
    cards_sent: Option<String>,
    /// An open background strip with a task running says its elapsed time again each second.
    ticks_at: Option<Instant>,
}

impl PaneUi {
    fn new(pane: &RunSession) -> PaneUi {
        let mut ui = PaneUi { dirty: true, ..PaneUi::default() };
        ui.view.draft = pane.draft.clone().unwrap_or_default();
        ui
    }

    /// The page loaded again: whatever it gets next is new to it.
    pub fn forget_sent(&mut self) {
        self.composer_sent = None;
        self.cards_sent = None;
        self.dirty = true;
    }

    /// `HasComposerContent`: written words or attachments.
    pub fn has_content(&self) -> bool {
        !self.view.draft.chars().all(char::is_whitespace) || !self.view.attachments.is_empty()
    }
}

/// C# `WebPane(id)`: the pane view of a shell or agent pane, made on first use; "Session not found." for any other id.
pub fn ensure(view: &mut View, id: Option<&str>) -> Result<String, String> {
    let pane = id.and_then(|id| view.snapshot.session(id)).filter(|s| matches!(s.kind(), "claude" | "shell")).cloned();
    let Some(pane) = pane else { return Err("Session not found.".into()) };
    let id = pane.id().to_string();
    view.panes.entry(id.clone()).or_insert_with(|| PaneUi::new(&pane));
    Ok(id)
}

/// The pane view of a pane that has one (made by [`ensure`]).
pub fn ui<'a>(view: &'a mut View, id: &str) -> Result<&'a mut PaneUi, String> {
    view.panes.get_mut(id).ok_or_else(|| "Session not found.".to_string())
}

/// `QueuePaneAlive`: the pane is still in the state and still has its view.
pub fn alive(view: &View, id: &str) -> bool {
    view.panes.contains_key(id) && view.snapshot.session(id).is_some()
}

/// The pane's waiting tool-permission requests as the run service holds them (C# `toolPermissions`).
pub fn requests(host: &Host, id: &str) -> Vec<ToolPermissionRequest> {
    host.runs().map(|r| r.service.pending_permissions(id)).unwrap_or_default()
}

/// What the window knows of the pane that the composer reads (C# `owner.*` members); the later stages' stay at rest.
fn window_rules(snapshot: &AppSnapshot, pane: &RunSession, pending: usize) -> WindowRules {
    WindowRules {
        pending_requests: pending,
        awaits_notice: mighty_graph::notice::awaits_notice(pane),
        status_line_enabled: snapshot.status_line_enabled,
        ..WindowRules::default()
    }
}

/// C# `WebComposerState` for a pane with a view.
pub fn composer_state(host: &Host, view: &mut View, id: &str) -> Option<ComposerState> {
    let pane = view.snapshot.session(id)?.clone();
    let workspace = view.snapshot.workspace(pane.workspace_id()).cloned();
    let runs = host.runs();
    let runtime = runs.and_then(|r| r.composer_runtime(pane.provider()));
    let efforts = runs.map(|r| r.efforts(&pane, workspace.as_ref(), &view.snapshot)).unwrap_or_default();
    let window = window_rules(&view.snapshot, &pane, requests(host, id).iter().filter(|r| r.state == "pending").count());
    let ui = view.panes.get_mut(id)?;
    ui.view.queue = ui.queue.items().to_vec();
    let inputs = ComposerInputs {
        locale: host.locale,
        now: DateTimeOffset::now(),
        pane: &pane,
        workspace: workspace.as_ref(),
        runtime: runtime.as_ref(),
        efforts: &efforts,
        view: &ui.view,
        window: &window,
    };
    Some(composer::composer_state(&inputs))
}

/// The pane's composer as the composer's rules read it (busy, can-send), for the methods that decide by it.
pub fn refreshed(host: &Host, view: &mut View, id: &str) -> Option<(bool, composer::Refreshed)> {
    let pane = view.snapshot.session(id)?.clone();
    let workspace = view.snapshot.workspace(pane.workspace_id()).cloned();
    let runs = host.runs();
    let runtime = runs.and_then(|r| r.composer_runtime(pane.provider()));
    let efforts = runs.map(|r| r.efforts(&pane, workspace.as_ref(), &view.snapshot)).unwrap_or_default();
    let window = window_rules(&view.snapshot, &pane, 0);
    let ui = view.panes.get_mut(id)?;
    ui.view.queue = ui.queue.items().to_vec();
    let inputs = ComposerInputs {
        locale: host.locale,
        now: DateTimeOffset::now(),
        pane: &pane,
        workspace: workspace.as_ref(),
        runtime: runtime.as_ref(),
        efforts: &efforts,
        view: &ui.view,
        window: &window,
    };
    Some((composer::busy(&inputs), composer::refresh(&inputs)))
}

/// Minutes east of UTC on this computer, for the plan times (C# converts to the local zone).
fn utc_offset_minutes() -> i32 {
    chrono::Local::now().offset().local_minus_utc() / 60
}

/// C# `WebCards(out ticks)` for a pane with a view: the cards in the composer's order, and whether the background strip ticks.
pub fn cards_of(host: &Host, view: &mut View, id: &str) -> Option<(Vec<Card>, bool)> {
    let pane = view.snapshot.session(id)?.clone();
    let requests = requests(host, id);
    let background_open = view.snapshot.background_work_open == Some(true);
    let ui = view.panes.get_mut(id)?;
    let busy = pane.status() == "running" || ui.view.starting || ui.view.queue_starting;
    // The plan card's draft follows the first waiting request (RenderPlan makes one for each new plan).
    let first = cards::card_requests(&requests, false).first().map(|r| (*r).clone());
    let plan = first.as_ref().filter(|r| r.can_answer_plan && r.plan().is_some());
    match plan {
        Some(request) if ui.plan_draft.as_ref().is_none_or(|d| d.request_id != request.id) => {
            ui.plan_draft = Some(PlanDraft { request_id: request.id.clone(), sending: false, error: None });
        }
        Some(_) => {}
        None => ui.plan_draft = None,
    }
    // `ComposerShowsPlanActions`: the Mighty diagram draws the waiting plan, so the composer shows its answers alone.
    let diagram_draws_plan =
        plan.is_some() && mighty_graph::view::diagram_plan_run_id(true, cards::in_mighty_diagram(&pane), pane.graph_runs()).is_some();
    // A command in the pane's terminal waiting for input (terminal.rs, C# `terminalPrompts`).
    let terminal_prompt = crate::terminal::prompt_input(host, id);
    let inputs = CardInputs {
        locale: host.locale,
        utc_offset_minutes: utc_offset_minutes(),
        pane: &pane,
        busy,
        styled: false,
        style_draws_tasks: false,
        diagram_draws_plan,
        requests: &requests,
        plan_draft: ui.plan_draft.as_ref(),
        background_open,
        background_rows: &[],
        antigravity: None,
        terminal_prompt: terminal_prompt.as_ref(),
        web_open: &[],
        login: None,
        status_line: None,
    };
    Some(cards::cards(&inputs))
}

/// One pass of the composer and cards pushes over the pane views (C# `FlushWebComposers`, `FlushWebCards`): a pane whose record
/// changed, or that something else marked, is read again; each model goes out when its JSON is not the one last sent.
pub fn flush(host: &Host) {
    // A pane selected reads its model list again (models.rs, C# RefreshSelectedPaneModels).
    crate::models::follow_selection(host);
    let mut view = host.view();
    if !view.ready {
        return;
    }
    let ids: Vec<String> = view.panes.keys().cloned().collect();
    for id in ids {
        let Some(session) = view.snapshot.session(&id).cloned() else {
            view.panes.remove(&id);
            // The closed pane's older requests read from its record go with it (history.rs).
            crate::history::forget(host, &id);
            continue;
        };
        let Some(ui) = view.panes.get_mut(&id) else { continue };
        let ticked = ui.ticks_at.is_some_and(|at| at.elapsed() >= Duration::from_secs(1));
        if !ui.dirty && !ticked && ui.seen.as_ref().is_some_and(|seen| Arc::ptr_eq(seen, &session)) {
            continue;
        }
        ui.dirty = false;
        ui.seen = Some(session);
        if let Some(state) = composer_state(host, &mut view, &id) {
            let text = serde_json::to_string(&state).unwrap_or_default();
            if let Some(ui) = view.panes.get_mut(&id)
                && ui.composer_sent.as_deref() != Some(text.as_str())
            {
                ui.composer_sent = Some(text.clone());
                host.push(&view, "composer", format!("{{\"kind\":\"composer\",\"state\":{text}}}"));
            }
        }
        if let Some((cards, ticks)) = cards_of(host, &mut view, &id) {
            let text = serde_json::to_string(&cards).unwrap_or_default();
            let Some(ui) = view.panes.get_mut(&id) else { continue };
            ui.ticks_at = ticks.then(Instant::now);
            if ui.cards_sent.as_deref() != Some(text.as_str()) {
                ui.cards_sent = Some(text.clone());
                let pane = serde_json::to_string(&id).unwrap_or_default();
                host.push(&view, "cards", format!("{{\"kind\":\"cards\",\"paneId\":{pane},\"cards\":{text}}}"));
            }
        }
    }
}
