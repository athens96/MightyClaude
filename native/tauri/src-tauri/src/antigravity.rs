//! The Antigravity card in the app (C# MainWindow.AntigravityPermission.cs, the card of MainWindow.WebShell.Cards.cs): what the cards
//! push shows of a pane's card (`WebAntigravityCard`: each refused action with its kind, target, rule and once-only words), and its
//! three answers (`antigravityAnswer` → `WebAnswerAntigravity` → `AnswerAntigravityPermission`): 거절 closes the card; an allow sends the
//! continuation on the pane's conversation now, or queues it behind a busy pane, with the card's rules put in place while that run goes
//! (이번만 허용) or for good (항상 허용) — the rules agy's settings get only when the user presses one of them.
//!
//! agy's settings and the app's ledger (`DesktopService.AntigravityRules`) are made here for the run service: the user's own settings,
//! the ledger in the profile with `--allow-state-write`, else in this machine's local folder for the profile.

use std::path::{Path, PathBuf};
use std::sync::Arc;

use mighty_composer::cards::{AntigravityAction, AntigravityInput};
use mighty_composer::queue::{self, RuntimeReading};
use mighty_run::antigravity::permissions::{self as flow, PermissionDecision, PermissionRequest};
use mighty_run::antigravity::{AntigravityRuleLeases, leases};
use mighty_run::model::StartRunRequest;
use serde_json::Value;

use crate::composer::session;
use crate::host::Host;
use crate::panes;

/// The ledger's file name in its folder (C#: `<profile>\antigravity-temporary-rules.json`).
pub const LEDGER: &str = "antigravity-temporary-rules.json";

/// `DesktopService`'s `AntigravityRules` and `AntigravityRuleSafety.AppDataFolder`: agy's settings at `settings` (None: the user's own,
/// `%USERPROFILE%\.gemini\antigravity-cli\settings.json`), the ledger at `ledger`; no file rule may reach the app's `data_dir`. The
/// swap that keeps the settings' ACL and attributes is `ReplaceFileW` on Windows.
pub fn rules(settings: Option<&Path>, ledger: &Path, data_dir: &Path) -> AntigravityRuleLeases {
    flow::set_app_data_folder(Some(data_dir.to_path_buf()));
    let settings = settings.map(Path::to_path_buf).unwrap_or_else(|| AntigravityRuleLeases::default_settings_path(None));
    #[cfg(windows)]
    let replace: leases::Replace = crate::host::replace_file_keeping;
    #[cfg(not(windows))]
    let replace: leases::Replace = leases::portable_replace;
    AntigravityRuleLeases::with_replace(&settings, Some(ledger), None, None, replace)
}

/// Where the ledger goes: the profile's own with `--allow-state-write`, else this machine's local folder for the profile.
pub fn ledger_path(data_dir: &Path, local_dir: &Path, profile_key: &str, writes: bool) -> PathBuf {
    if writes { data_dir.join(LEDGER) } else { local_dir.join("antigravity").join(profile_key).join(LEDGER) }
}

/// C# `WebRequestCard`'s Antigravity half: the pane's card in words, its reason and whether an answer is in flight.
pub fn card_input(host: &Host, id: &str) -> Option<AntigravityInput> {
    let service = &host.runs()?.service;
    let card = service.antigravity_card(id)?;
    let actions = card
        .actions
        .iter()
        .map(|a| AntigravityAction {
            heading: format!("{} · {}", flow::kind_label(&a.kind), a.display_name),
            target: a.target.clone(),
            code: a.kind == "command" || a.kind == "file",
            rule: flow::rule_line(a),
            once_only: flow::once_only_text(a),
        })
        .collect();
    Some(AntigravityInput {
        card_id: card.id.clone(),
        run_id: card.run_id.clone(),
        actions,
        reason: service.antigravity_error(id),
        busy: service.antigravity_answering(id, &card),
        can_allow: card.can_allow(),
        can_allow_always: card.can_allow_always(),
    })
}

/// `Enum.TryParse<AntigravityPermissionDecision>(text, ignoreCase: true)`: the names, or their numbers.
pub fn decision(text: Option<&str>) -> Option<PermissionDecision> {
    let text = text?.trim();
    PermissionDecision::parse(text).or(match text {
        "0" => Some(PermissionDecision::Once),
        "1" => Some(PermissionDecision::Always),
        "2" => Some(PermissionDecision::Deny),
        _ => None,
    })
}

fn mark(host: &Host, id: &str) {
    if let Some(ui) = host.view().panes.get_mut(id) {
        ui.dirty = true;
    }
}

/// `WebAnswerAntigravity`: the card the page shows answered through [`answer`]; why nothing happened, or None. The card is drawn again
/// either way.
pub async fn web_answer(host: &Arc<Host>, id: &str, card_id: &str, decision: PermissionDecision) -> Option<String> {
    let current = host.runs().and_then(|r| r.service.antigravity_card(id));
    let outcome = match current.filter(|card| card.id == card_id) {
        Some(card) => answer(host, id, &card, decision).await,
        None => Some(host.locale.get("remote.error.notPendingPermission")),
    };
    mark(host, id);
    outcome
}

/// `PaneView.MobileBusy`: the pane runs, starts or its queue starts one.
fn busy(host: &Host, id: &str) -> bool {
    let mut view = host.view();
    let running = view.snapshot.session(id).is_some_and(|s| s.status() == "running");
    let ui = view.panes.get_mut(id);
    running
        || ui.is_some_and(|ui| ui.view.starting || ui.view.queue_starting)
        || host.runs().is_some_and(|r| r.service.is_session_running(id))
}

/// `RunBlockReason(PaneRuntime(provider))`: why the pane cannot run for good (its CLI missing, a mode it cannot run), None while it may.
fn run_block(host: &Host, id: &str) -> Option<String> {
    let pane = session(&host.view(), id).ok()?;
    let runtime = host.runs()?.runtime(pane.provider()).map(|r| RuntimeReading {
        name: r.name.clone(),
        available: r.available,
        detail: r.detail.clone(),
        permission_modes: r.capabilities.permission_modes.clone(),
    });
    queue::run_block_reason(&pane, runtime.as_ref(), &host.locale)
}

/// C# `AnswerAntigravityPermission`: the card's buttons. Deny closes the card. An allow sends the continuation on the pane's conversation
/// now, or queues it behind a busy pane, with the card's rules: put in place while that run goes (once) or for good (always). Returns why
/// nothing happened, which the card shows too; None when it went through.
pub async fn answer(host: &Arc<Host>, id: &str, request: &PermissionRequest, decision: PermissionDecision) -> Option<String> {
    let not_pending = host.locale.get("remote.error.notPendingPermission");
    let Ok(engine) = host.engine() else { return Some(not_pending) };
    let service = engine.service.clone();
    if !panes::alive(&host.view(), id) || service.antigravity_answering(id, request) {
        return Some(not_pending);
    }
    let Some(card) = service.pending_antigravity_card(id, &request.id, &request.run_id) else { return Some(not_pending) };
    service.set_antigravity_error(id, None);
    if decision == PermissionDecision::Deny {
        service.take_antigravity_card(id);
        return None;
    }
    let refuse = |reason: String| {
        service.set_antigravity_error(id, Some(&reason));
        Some(reason)
    };
    // No rule, or 항상 허용 for a rule that may be allowed only once.
    if let Some(refusal) = flow::refusal(&card, decision) {
        return refuse(refusal);
    }
    let Some(continuation) = flow::continuation(&card, decision) else { return refuse(host.locale.get("antigravity.permission.noRule")) };
    if !service.begin_antigravity_answer(id, &card) {
        return Some(not_pending);
    }
    let outcome = async {
        // A local pane's rules go in this computer's settings: one that cannot take them is refused here, before anything is sent.
        if !service.is_remote_session(id)
            && let Err(error) = service.check_antigravity_settings().await
        {
            return refuse(error.message);
        }
        let pane = session(&host.view(), id).ok();
        let pane = match pane {
            Some(pane)
                if panes::alive(&host.view(), id)
                    && service.pending_antigravity_card(id, &card.id, &card.run_id).is_some()
                    && pane.kind() == "claude"
                    && pane.provider() == "gemini" =>
            {
                pane
            }
            _ => return Some(not_pending),
        };
        if let Some(blocked) = run_block(host, id) {
            return refuse(blocked);
        }
        if busy(host, id) {
            // The composer's rule for a busy pane: it waits in the queue, and its rules go in place when that run starts.
            let queued = {
                let mut view = host.view();
                let Ok(ui) = panes::ui(&mut view, id) else { return Some(not_pending) };
                if ui.queue.items().len() >= queue::MAXIMUM_ITEMS {
                    Err(host.locale.get_with("queue.fullCount", &[("count", &queue::MAXIMUM_ITEMS.to_string())]))
                } else {
                    match ui.queue.add(&continuation.prompt, vec![], None, true, true, &host.locale) {
                        Ok(item) => {
                            let kept: Vec<String> = ui.queue.items().iter().map(|i| i.id.clone()).collect();
                            ui.continuations.retain(|item, _| kept.contains(item));
                            ui.continuations.insert(item.id, (continuation.grant.clone(), card.clone()));
                            ui.dirty = true;
                            Ok(())
                        }
                        Err(error) => Err(error.to_string()),
                    }
                }
            };
            if let Err(reason) = queued {
                return refuse(reason);
            }
            service.take_antigravity_card(id);
            if !busy(host, id) {
                crate::composer::drain_queue(host.clone(), id.to_string());
            }
            return None;
        }
        let mut run = {
            let view = host.view();
            let workspace = view.snapshot.workspace(pane.workspace_id()).cloned();
            let mut run = StartRunRequest::new(id, pane.workspace_id(), pane.kind(), &continuation.prompt);
            run.registered_models = crate::runs::registered_models_for(pane.provider(), workspace.as_ref(), &view.snapshot);
            run.model = pane.model().to_string();
            run.provider = pane.provider().to_string();
            run.settings = pane.settings.clone();
            run.resume_id = pane.resume_id.clone();
            run
        };
        run.antigravity_grant = Some(continuation.grant.clone());
        run.antigravity_card = Some(card.clone());
        service.take_antigravity_card(id);
        if let Ok(ui) = panes::ui(&mut host.view(), id) {
            ui.view.starting = true;
            ui.dirty = true;
        }
        let started = service.start_reached(run).await;
        if let Some(ui) = host.view().panes.get_mut(id) {
            ui.view.starting = false;
            ui.dirty = true;
        }
        match started {
            Ok(_) => None,
            Err(error) => {
                // It did not start (agy's settings refused the rules meanwhile): the card comes back with the reason.
                if !panes::alive(&host.view(), id) {
                    return Some(error.message().to_string());
                }
                service.restore_antigravity_card(id, card.clone());
                refuse(error.message().to_string())
            }
        }
    }
    .await;
    service.end_antigravity_answer(id, &card);
    outcome
}

/// The page method's arguments read as `RegisterCardMethods` reads them.
pub fn arguments(args: &Value) -> (Option<&str>, Option<&str>, Option<&str>) {
    let text = |key: &str| args.get(key).and_then(Value::as_str);
    (text("paneId"), text("cardId"), text("decision"))
}

#[cfg(test)]
mod tests {
    use super::*;

    /// `Enum.TryParse<AntigravityPermissionDecision>(decision, ignoreCase: true)` as `antigravityAnswer` reads the page's words.
    #[test]
    fn the_page_answers_once_always_or_deny() {
        assert_eq!(decision(Some("Once")), Some(PermissionDecision::Once));
        assert_eq!(decision(Some("always")), Some(PermissionDecision::Always));
        assert_eq!(decision(Some(" DENY ")), Some(PermissionDecision::Deny));
        assert_eq!(decision(Some("1")), Some(PermissionDecision::Always));
        assert_eq!(decision(Some("allow")), None);
        assert_eq!(decision(None), None);
        let json = serde_json::json!({"paneId": "p", "cardId": "agy-1", "decision": "Once"});
        assert_eq!(arguments(&json), (Some("p"), Some("agy-1"), Some("Once")));
    }

    /// The ledger is the profile's only when the app may write it; else this machine's own for the profile.
    #[test]
    fn the_ledger_stays_out_of_the_profile_without_writes() {
        let (profile, local) = (Path::new("profile"), Path::new("local"));
        assert_eq!(ledger_path(profile, local, "key", true), profile.join(LEDGER));
        assert_eq!(ledger_path(profile, local, "key", false), local.join("antigravity").join("key").join(LEDGER));
    }
}
