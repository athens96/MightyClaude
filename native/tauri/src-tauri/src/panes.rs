//! The never-shown pane view of each pane the page writes in (C# `PaneView`, made by `WebPane`, MainWindow.WebShell.Composer.cs:24):
//! its editor's draft and attachments, its flags, its queue, its plan card's draft and its Mighty view's own state. From it and the
//! state, the composer's model (`composer_state`, C# `WebComposerState`) and the cards (`cards`, C# `WebCards`) are worked out by
//! mighty-composer's rules; [`flush`] pushes each when its JSON changed (`composer` and `cards`, 30 ms, C# `FlushWebComposers`,
//! `FlushWebCards`), the cards again each second while an open background strip has a task running.
//!
//! W6's parts give the update holds (updates.rs), the missing CLI's install (setup.rs), the sign-in card (accounts.rs) and the status
//! line (status_line.rs) through their seams; what later stages give stays at its resting value (no web-open prompts, no style): README
//! "Pending inputs". The Antigravity card is antigravity.rs's.

use std::collections::HashMap;
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
    /// The queued continuations of Antigravity cards (C# `QueuedComposerInput.AntigravityGrant`/`AntigravityCard`), by queued item.
    pub continuations: HashMap<String, (mighty_run::antigravity::AllowGrant, mighty_run::antigravity::PermissionRequest)>,
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

    /// C8: the grants and cards of queued continuations whose items left the queue (removed, cleared by a stop, settled, started) go with
    /// them.
    pub fn forget_unqueued(&mut self) {
        let queued: std::collections::HashSet<&str> = self.queue.items().iter().map(|item| item.id.as_str()).collect();
        self.continuations.retain(|item, _| queued.contains(item.as_str()));
    }

    /// S-L3: the queued continuations of the card `card_id` lose their grant — the items and their rules leave the queue, so no later start
    /// puts in place rules the user refused. True when one was there.
    pub fn revoke_continuations(&mut self, card_id: &str) -> bool {
        let items: Vec<String> =
            self.continuations.iter().filter(|(_, (_, card))| card.id == card_id).map(|(item, _)| item.clone()).collect();
        for item in &items {
            self.queue.remove(item);
            self.continuations.remove(item);
        }
        !items.is_empty()
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

/// What the window knows of the pane that the composer reads (C# `owner.*` members); the later stages' stay at rest. A Claude pane
/// whose catalogue was never scanned is scanned for its "send to" switch (ask.rs, C# `RefreshAskCatalog(pane, whenStale: false)`).
/// The update holds are W6-D's (updates.rs: `BackgroundUpdateHolds`, `ManualMutationBlockReason`, `ManualUpdateReason`), the missing
/// CLI's install W6-B's (setup.rs: `SetupItemForMissingCli`), each read with the state locked.
fn window_rules(host: &Host, snapshot: &AppSnapshot, pane: &RunSession, pending: usize) -> WindowRules {
    crate::ask::refresh_catalog(host, snapshot, pane, false);
    WindowRules {
        background_update_holds: crate::updates::holds(host, snapshot, pane),
        manual_mutation_block: crate::updates::manual_block(host, snapshot, pane),
        manual_update_reason: crate::updates::manual_update(host, snapshot, pane),
        setup_item_for_missing_cli: crate::setup::item_for_missing_cli(host, snapshot, pane).is_some(),
        pending_requests: pending,
        awaits_notice: mighty_graph::notice::awaits_notice(pane),
        status_line_enabled: snapshot.status_line_enabled,
        ask_targets: crate::ask::targets_for(host, snapshot, pane),
        ask_notice: crate::ask::notice(host, pane.id()),
        ..WindowRules::default()
    }
}

/// Every pane's composer and cards looked at again on the next pass (a part's input changed: an update's hold, a sign-in card, a
/// status line).
#[allow(dead_code, reason = "a seam W6-B, W6-C and W6-D (their inputs changed) calls")]
pub fn mark_all(host: &Host) {
    host.panes_changed();
}

/// C# `WebComposerState` for a pane with a view.
pub fn composer_state(host: &Host, view: &mut View, id: &str) -> Option<ComposerState> {
    let pane = view.snapshot.session(id)?.clone();
    let workspace = view.snapshot.workspace(pane.workspace_id()).cloned();
    let runs = host.runs();
    let runtime = runs.and_then(|r| r.composer_runtime(pane.provider()));
    let efforts = runs.map(|r| r.efforts(&pane, workspace.as_ref(), &view.snapshot)).unwrap_or_default();
    let window = window_rules(host, &view.snapshot, &pane, requests(host, id).iter().filter(|r| r.state == "pending").count());
    let ui = view.panes.get_mut(id)?;
    ui.forget_unqueued();
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
    let window = window_rules(host, &view.snapshot, &pane, 0);
    let ui = view.panes.get_mut(id)?;
    ui.forget_unqueued();
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
    // W6's inputs, read with the state locked: an automatic update's hold (updates.rs, W6-D), the sign-in card (accounts.rs, W6-C),
    // the status line (status_line.rs, W6-B).
    let holds = crate::updates::holds(host, &view.snapshot, &pane);
    let login = crate::accounts::login_input(host, &view.snapshot, &pane);
    let status_line = crate::status_line::input(host, &pane);
    let ui = view.panes.get_mut(id)?;
    let busy = pane.status() == "running" || ui.view.starting || ui.view.queue_starting || holds;
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
    // The pane's Antigravity card (antigravity.rs, C# `AntigravityCard`/`AntigravityError`/`AntigravityAnswering`).
    let antigravity = crate::antigravity::card_input(host, id);
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
        antigravity: antigravity.as_ref(),
        terminal_prompt: terminal_prompt.as_ref(),
        web_open: &[],
        login: login.as_ref(),
        status_line: status_line.as_ref(),
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

#[cfg(test)]
mod tests {
    use mighty_core::locale::Locale;
    use mighty_run::antigravity::{AllowGrant, PermissionRequest};

    use super::*;

    fn card(id: &str) -> PermissionRequest {
        PermissionRequest { id: id.into(), run_id: "run-1".into(), actions: vec![], received_at: None }
    }

    /// A pane whose queue holds a plain request and the continuation of the card `card_id`.
    fn queued(card_id: &str) -> (PaneUi, String, String) {
        let mut ui = PaneUi::default();
        let locale = Locale::new("en");
        let plain = ui.queue.add("plain", vec![], None, false, false, &locale).unwrap().id;
        let continuation = ui.queue.add("go on", vec![], None, true, true, &locale).unwrap().id;
        let grant = AllowGrant::new(card_id, &["command(echo hi)"], true);
        ui.continuations.insert(continuation.clone(), (grant, card(card_id)));
        (ui, plain, continuation)
    }

    /// C8: a continuation's grant and card go with its item, whichever way it leaves the queue.
    #[test]
    fn a_continuation_leaves_with_its_item() {
        let (mut ui, plain, continuation) = queued("agy-1");
        ui.queue.remove(&plain);
        ui.forget_unqueued();
        assert!(ui.continuations.contains_key(&continuation), "another item's removal keeps it");
        ui.queue.remove(&continuation);
        ui.forget_unqueued();
        assert!(ui.continuations.is_empty(), "its own removal takes it");
        let (mut ui, _, _) = queued("agy-1");
        ui.queue.clear();
        ui.forget_unqueued();
        assert!(ui.continuations.is_empty(), "a stop's clear takes it");
    }

    /// S-L3: denying a card (or its start failing) revokes its queued continuation: the item and its rules leave the queue; another card's
    /// continuation and the plain requests stay.
    #[test]
    fn a_denied_card_revokes_its_queued_continuation() {
        let (mut ui, plain, continuation) = queued("agy-1");
        assert!(!ui.revoke_continuations("agy-2"), "another card's deny revokes nothing");
        assert!(ui.revoke_continuations("agy-1"), "this card's deny revokes its continuation");
        assert!(ui.continuations.is_empty() && !ui.queue.items().iter().any(|i| i.id == continuation), "its grant and its item are gone");
        assert!(ui.queue.items().iter().any(|i| i.id == plain), "a plain request stays");
    }
}
