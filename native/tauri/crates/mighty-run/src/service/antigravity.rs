//! Antigravity permission cards in the desktop service (C# DesktopService.Antigravity.cs, M/MightyCode/AppStore+AntigravityPermissions.swift):
//! a headless agy turn is refused what it would have asked about, so the pane asks once the turn is over. The card of each pane lives
//! here, beside the rules a local pane's continuation holds; an allow puts rules in agy's settings for a follow-up turn in the same
//! conversation (a remote pane's host does that on its own computer, W8).

use std::collections::{HashMap, HashSet};

use super::{RunService, ServiceEvent, Shared};
use crate::antigravity::{AntigravityRuleLeases, Lease, PermissionBoard, PermissionRequest, SettingsError, leases::ApplyError};
use crate::model::{RunEvent, StartRunRequest};
use crate::{Result, RunError, lock};

/// What the service keeps of every pane's card (C#'s `antigravityBoard`, `antigravityErrors`, `antigravityResponses`,
/// `antigravityLeases`), under one lock (`antigravityGate`).
#[derive(Default)]
pub(super) struct Desk {
    board: PermissionBoard,
    /// Why a pane's last answer did not go through, shown on its card.
    errors: HashMap<String, String>,
    /// Answers in flight, by pane, run and card.
    responses: HashSet<String>,
    /// The temporary rules each local pane's run holds, released when it ends.
    leases: HashMap<String, Lease>,
}

/// `AnswerKey`.
fn answer_key(session_id: &str, card: &PermissionRequest) -> String {
    format!("{session_id}|{}|{}", card.run_id, card.id)
}

/// A lease let go off the caller's thread (C#'s `Task.Run(() => AntigravityRules.Release(lease))`): a removal may pause between tries.
fn release_later(rules: &AntigravityRuleLeases, lease: Lease) {
    let rules = rules.clone();
    std::thread::spawn(move || rules.release(&lease));
}

impl Shared {
    /// `AntigravityCardChanged`: a pane's card came, went or changed its reason.
    fn card_changed(&self, session_id: &str) {
        self.send(ServiceEvent::AntigravityCard(session_id.to_string()));
    }

    /// `ReceiveAntigravity`: a card arrives before its run's final status; a later run's end clears it, and the run's own temporary rules
    /// leave.
    pub(super) fn receive_antigravity(&self, value: &RunEvent) {
        let mut changed = false;
        if value.kind == "antigravityPermission"
            && let Some(card) = value.antigravity_permission.as_ref().filter(|c| !c.actions.is_empty())
        {
            let agy = {
                let state = lock(&self.state);
                !state.closing
                    && state
                        .snapshot
                        .sessions()
                        .iter()
                        .any(|s| s.id() == value.session_id && s.kind() == "claude" && s.provider() == "gemini")
            };
            if agy {
                let mut desk = lock(&self.antigravity);
                desk.board.received(card.clone(), &value.session_id);
                desk.errors.remove(&value.session_id);
                changed = true;
            }
        } else if value.kind == "status" && matches!(value.status.as_deref(), Some("completed" | "error" | "stopped")) {
            let lease = {
                let mut desk = lock(&self.antigravity);
                changed = desk.board.run_ended(&value.session_id);
                desk.leases.remove(&value.session_id)
            };
            if let Some(lease) = lease {
                release_later(self.rules(), lease);
            }
        }
        if changed {
            self.card_changed(&value.session_id);
        }
    }

    /// `RestoreAntigravityCard`: puts back a card whose answer could not run (the pane still there); the next run's end clears it as usual.
    pub(super) fn restore_antigravity_card(&self, session_id: &str, card: PermissionRequest) {
        let live = {
            let state = lock(&self.state);
            !state.closing && state.snapshot.session(session_id).is_some()
        };
        if !live {
            return;
        }
        let restored = lock(&self.antigravity).board.restore(card, session_id);
        if restored {
            self.card_changed(session_id);
        }
    }

    /// `SetAntigravityError`: the reason the card shows; None clears it.
    pub(super) fn set_antigravity_error(&self, session_id: &str, reason: Option<&str>) {
        {
            let mut desk = lock(&self.antigravity);
            match reason {
                Some(reason) => desk.errors.insert(session_id.to_string(), reason.to_string()),
                None => desk.errors.remove(session_id),
            };
        }
        self.card_changed(session_id);
    }

    /// `ContinuationFailed`: an allowed card's continuation that could not start (a queued one too) — the card comes back with the reason.
    pub(super) fn continuation_failed(&self, request: &StartRunRequest, reason: &str) {
        let Some(card) = &request.antigravity_card else { return };
        self.restore_antigravity_card(&request.session_id, card.clone());
        let back = lock(&self.antigravity).board.get(&request.session_id) == Some(card);
        if back {
            self.set_antigravity_error(&request.session_id, Some(reason));
        }
    }

    /// `ForgetAntigravity`: a closed pane's card and reason go with it.
    pub(super) fn forget_antigravity(&self, session_id: &str) {
        let had = {
            let mut desk = lock(&self.antigravity);
            desk.errors.remove(session_id);
            desk.board.remove(session_id).is_some()
        };
        if had {
            self.card_changed(session_id);
        }
    }
}

impl RunService {
    /// `AntigravityRules`: agy's settings, shared by this computer's panes.
    pub fn antigravity_rules(&self) -> &AntigravityRuleLeases {
        self.shared.rules()
    }

    /// `AntigravityCard`.
    pub fn antigravity_card(&self, session_id: &str) -> Option<PermissionRequest> {
        lock(&self.shared.antigravity).board.get(session_id).cloned()
    }

    /// `PendingAntigravityCard`: the pane's card when it is this one.
    pub fn pending_antigravity_card(&self, session_id: &str, request_id: &str, run_id: &str) -> Option<PermissionRequest> {
        lock(&self.shared.antigravity).board.pending(session_id, request_id, run_id).cloned()
    }

    /// `AntigravityError`.
    pub fn antigravity_error(&self, session_id: &str) -> Option<String> {
        lock(&self.shared.antigravity).errors.get(session_id).cloned()
    }

    /// `AntigravityAnswering`: an answer to `card` is in flight.
    pub fn antigravity_answering(&self, session_id: &str, card: &PermissionRequest) -> bool {
        lock(&self.shared.antigravity).responses.contains(&answer_key(session_id, card))
    }

    /// `BeginAntigravityAnswer`: the answer to `card` starts; false when one is in flight already.
    pub fn begin_antigravity_answer(&self, session_id: &str, card: &PermissionRequest) -> bool {
        let begun = lock(&self.shared.antigravity).responses.insert(answer_key(session_id, card));
        if begun {
            self.shared.card_changed(session_id);
        }
        begun
    }

    /// `EndAntigravityAnswer`.
    pub fn end_antigravity_answer(&self, session_id: &str, card: &PermissionRequest) {
        lock(&self.shared.antigravity).responses.remove(&answer_key(session_id, card));
        self.shared.card_changed(session_id);
    }

    /// `SetAntigravityError`: the reason the card shows; None clears it.
    pub fn set_antigravity_error(&self, session_id: &str, reason: Option<&str>) {
        self.shared.set_antigravity_error(session_id, reason);
    }

    /// `TakeAntigravityCard`: the card closes (denied, or its continuation is on its way).
    pub fn take_antigravity_card(&self, session_id: &str) -> Option<PermissionRequest> {
        let card = lock(&self.shared.antigravity).board.remove(session_id);
        if card.is_some() {
            self.shared.card_changed(session_id);
        }
        card
    }

    /// `RestoreAntigravityCard`.
    pub fn restore_antigravity_card(&self, session_id: &str, card: PermissionRequest) {
        self.shared.restore_antigravity_card(session_id, card);
    }

    /// `CheckAntigravitySettingsAsync`: reads agy's settings the way an allow would, off the caller's thread, and changes nothing; why
    /// they cannot take a rule.
    pub async fn check_antigravity_settings(&self) -> std::result::Result<(), SettingsError> {
        let rules = self.shared.rules().clone();
        tokio::task::spawn_blocking(move || rules.check())
            .await
            .unwrap_or_else(|error| Err(SettingsError { kind: "unreadable".into(), message: error.to_string() }))
    }

    /// `IsRemoteSession`: the pane is in another computer's workspace.
    pub fn is_remote_session(&self, session_id: &str) -> bool {
        self.remote_pane(session_id)
    }

    /// `HoldAntigravityRules`: a local continuation's rules go in place before its run starts; held from here, the run's end (or its
    /// failed start) releases them.
    pub(super) async fn hold_antigravity_rules(&self, request: &StartRunRequest) -> Result<()> {
        let Some(grant) = request.antigravity_grant.clone() else { return Ok(()) };
        if request.provider != "gemini" || request.kind != "claude" {
            return Ok(());
        }
        let rules = self.shared.rules().clone();
        let applied = tokio::task::spawn_blocking(move || rules.apply(&grant))
            .await
            .map_err(|error| RunError::State(error.to_string()))?
            .map_err(|error| match error {
            ApplyError::Cancelled => RunError::Cancelled(String::new()),
            ApplyError::Settings(error) => RunError::State(error.message),
        })?;
        let Some(lease) = applied else { return Ok(()) };
        // Quitting meanwhile: the rules go again rather than outlive the app.
        if lock(&self.shared.state).closing {
            let rules = self.shared.rules().clone();
            let _ = tokio::task::spawn_blocking(move || rules.release(&lease)).await;
            return Err(RunError::Cancelled(String::new()));
        }
        let previous = lock(&self.shared.antigravity).leases.insert(request.session_id.clone(), lease);
        if let Some(previous) = previous {
            release_later(self.shared.rules(), previous);
        }
        Ok(())
    }

    /// `ReleaseAntigravityRules`: quit — the temporary rules leave agy's settings with the runs that held them.
    pub(super) async fn release_antigravity_rules(&self) {
        lock(&self.shared.antigravity).leases.clear();
        let rules = self.shared.rules().clone();
        let _ = tokio::task::spawn_blocking(move || rules.release_all()).await;
    }
}
