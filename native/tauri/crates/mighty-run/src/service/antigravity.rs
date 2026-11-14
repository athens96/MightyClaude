//! Antigravity permission cards in the desktop service (C# DesktopService.Antigravity.cs, M/MightyCode/AppStore+AntigravityPermissions.swift):
//! a headless agy turn is refused what it would have asked about, so the pane asks once the turn is over. The card of each pane lives
//! here, beside the rules a local pane's continuation holds; an allow puts rules in agy's settings for a follow-up turn in the same
//! conversation (a remote pane's host does that on its own computer, W8).

use std::collections::{HashMap, HashSet};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};

use super::{RunService, ServiceEvent, Shared};
use crate::antigravity::{AntigravityRuleLeases, Lease, PermissionBoard, PermissionRequest, SettingsError, leases::ApplyError};
use crate::model::{RunEvent, StartRunRequest};
use crate::{Result, RunError, lock};

/// What the service keeps of every pane's card (C#'s `antigravityBoard`, `antigravityErrors`, `antigravityResponses`,
/// `antigravityLeases`), under one lock (`antigravityGate`). The service's state lock is taken before it, never after.
#[derive(Default)]
pub(super) struct Desk {
    board: PermissionBoard,
    /// Why a pane's last answer did not go through, shown on its card.
    errors: HashMap<String, String>,
    /// Answers in flight, by pane, run and card.
    responses: HashSet<String>,
    /// The temporary rules each local pane's run holds, with the start that holds them ([`next_holder`]), released when that run ends.
    leases: HashMap<String, (u64, Lease)>,
}

static NEXT_HOLDER: AtomicU64 = AtomicU64::new(1);

/// A new start's holder (C10): the route it makes is told apart from the one before and after it in the same pane.
pub(super) fn next_holder() -> u64 {
    NEXT_HOLDER.fetch_add(1, Ordering::Relaxed)
}

/// C# `InitializeAsync`'s `RemoveLeftovers` (C2): what a crash left in agy's settings goes on a thread of its own, and `done` says when;
/// no agy run starts before ([`RunService::hold_antigravity_rules`]).
pub(super) fn remove_leftovers(rules: AntigravityRuleLeases, done: tokio::sync::watch::Sender<bool>) {
    std::thread::spawn(move || {
        // A panic on the way still lets runs start: what it could not take out stays recorded for the next launch.
        let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| rules.remove_leftovers()));
        let _ = done.send(true);
    });
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
                // The route the next start made is already there when its rules are (C10): the rules of the start that holds the route
                // now are its own, not the ended run's, and stay. Both locks at once, so no start's rules land between the look and the
                // release.
                let state = lock(&self.state);
                let current = state.routes.get(&value.session_id).map(|route| route.holder);
                let mut desk = lock(&self.antigravity);
                changed = desk.board.run_ended(&value.session_id);
                let ended = desk.leases.get(&value.session_id).is_some_and(|(holder, _)| Some(*holder) != current);
                drop(state);
                if ended { desk.leases.remove(&value.session_id).map(|(_, lease)| lease) } else { None }
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

    /// `HoldAntigravityRules`: a local agy run starts only once a crash's leftovers are out of agy's settings (C2); a continuation's rules
    /// go in place before its run starts, held from here: the run's end (or its failed start) releases them. The lease is kept on the
    /// desk by the blocking work that made it (L6), so a start abandoned while the rules went in never loses it.
    pub(super) async fn hold_antigravity_rules(self: &Arc<Self>, request: &StartRunRequest) -> Result<()> {
        if request.provider != "gemini" || request.kind != "claude" {
            return Ok(());
        }
        let mut removed = self.leftovers_removed.clone();
        // A removal that ended without saying so (its thread gone) lets runs start too.
        let _ = removed.wait_for(|done| *done).await;
        let Some(grant) = request.antigravity_grant.clone() else { return Ok(()) };
        let holder = lock(&self.state).routes.get(&request.session_id).map_or(0, |route| route.holder);
        let (shared, session) = (self.clone(), request.session_id.clone());
        let held = tokio::task::spawn_blocking(move || -> std::result::Result<(), ApplyError> {
            let rules = shared.rules();
            let Some(lease) = rules.apply(&grant)? else { return Ok(()) };
            // Quitting meanwhile: the rules go again rather than outlive the app.
            if lock(&shared.state).closing {
                rules.release(&lease);
                return Err(ApplyError::Cancelled);
            }
            let previous = lock(&shared.antigravity).leases.insert(session, (holder, lease));
            if let Some((_, previous)) = previous {
                release_later(rules, previous);
            }
            Ok(())
        });
        held.await.map_err(|error| RunError::State(error.to_string()))?.map_err(|error| match error {
            ApplyError::Cancelled => RunError::Cancelled(String::new()),
            ApplyError::Settings(error) => RunError::State(error.message),
        })
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

    /// `HoldAntigravityRules` ([`Shared::hold_antigravity_rules`]).
    pub(super) async fn hold_antigravity_rules(&self, request: &StartRunRequest) -> Result<()> {
        self.shared.hold_antigravity_rules(request).await
    }

    /// The temporary rules the pane's run holds now (its lease), None when it holds none.
    pub fn antigravity_lease(&self, session_id: &str) -> Option<Vec<String>> {
        lock(&self.shared.antigravity).leases.get(session_id).map(|(_, lease)| lease.rules.clone())
    }

    /// The leftovers a crash left in agy's settings are out (C2): agy runs may start.
    pub async fn antigravity_leftovers_removed(&self) {
        let mut removed = self.shared.leftovers_removed.clone();
        let _ = removed.wait_for(|done| *done).await;
    }

    /// `ReleaseAntigravityRules`: quit — the temporary rules leave agy's settings with the runs that held them.
    pub(super) async fn release_antigravity_rules(&self) {
        lock(&self.shared.antigravity).leases.clear();
        let rules = self.shared.rules().clone();
        let _ = tokio::task::spawn_blocking(move || rules.release_all()).await;
    }
}

#[cfg(test)]
mod tests {
    use std::path::{Path, PathBuf};
    use std::sync::atomic::AtomicBool;
    use std::sync::{Condvar, Mutex, OnceLock};
    use std::time::{Duration, Instant};

    use mighty_core::model::AppSnapshot;

    use super::super::{Route, State};
    use super::*;
    use crate::antigravity::{AllowGrant, leases};

    const SETTINGS: &str = "{\n  \"permissions\": {\n    \"allow\": [\n      \"command(ls)\"\n    ]\n  }\n}\n";

    /// A swap a check holds shut until it lets it go, and that says when it was reached.
    struct Gate {
        reached: AtomicBool,
        open: Mutex<bool>,
        opened: Condvar,
    }

    impl Gate {
        const fn new() -> Gate {
            Gate { reached: AtomicBool::new(false), open: Mutex::new(false), opened: Condvar::new() }
        }
        fn pass(&self) {
            self.reached.store(true, Ordering::SeqCst);
            let mut open = lock(&self.open);
            while !*open {
                open = self.opened.wait(open).unwrap_or_else(|poisoned| poisoned.into_inner());
            }
        }
        fn let_go(&self) {
            *lock(&self.open) = true;
            self.opened.notify_all();
        }
    }

    fn folder(name: &str) -> PathBuf {
        let folder =
            std::env::temp_dir().join(format!("mighty-run-service-agy-{name}-{}-{}", std::process::id(), mighty_core::wire::new_id()));
        std::fs::create_dir_all(folder.join(".gemini").join("antigravity-cli")).unwrap();
        std::fs::write(settings_of(&folder), SETTINGS).unwrap();
        folder
    }

    fn settings_of(folder: &Path) -> PathBuf {
        folder.join(".gemini").join("antigravity-cli").join("settings.json")
    }

    fn rules(folder: &Path, replace: leases::Replace) -> AntigravityRuleLeases {
        let probe: Arc<dyn mighty_platform::PathProbe> = Arc::new(mighty_platform::unsupported::Unsupported);
        let ledger = folder.join("app").join("antigravity-temporary-rules.json");
        AntigravityRuleLeases::with_ports(&settings_of(folder), Some(&ledger), Some(vec![]), Some(Duration::ZERO), replace, probe)
    }

    /// The service's shared part over `rules`, the leftovers out already or not.
    fn shared(rules: AntigravityRuleLeases, removed: bool) -> (Arc<Shared>, tokio::sync::watch::Sender<bool>) {
        let (events, _receiver) = tokio::sync::mpsc::unbounded_channel();
        let (done, leftovers_removed) = tokio::sync::watch::channel(removed);
        let shared = Arc::new(Shared {
            state: Mutex::new(State { snapshot: AppSnapshot::default(), routes: HashMap::new(), closing: false }),
            events,
            pending: Mutex::new(HashMap::new()),
            antigravity: Mutex::new(Desk::default()),
            antigravity_rules: OnceLock::from(rules),
            leftovers_removed,
        });
        (shared, done)
    }

    fn continuation(session: &str) -> StartRunRequest {
        let mut request = StartRunRequest::new(session, "ws", "claude", "go on").with_provider("gemini");
        request.antigravity_grant = Some(AllowGrant::new("agy-1", &["command(echo hi)"], true));
        request
    }

    async fn until(mut condition: impl FnMut() -> bool, what: &str) {
        let end = Instant::now() + Duration::from_secs(10);
        while !condition() {
            assert!(Instant::now() < end, "never: {what}");
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
    }

    static LEFTOVERS: Gate = Gate::new();
    fn leftovers_swap(temporary: &Path, target: &Path) -> std::io::Result<()> {
        LEFTOVERS.pass();
        leases::portable_replace(temporary, target)
    }

    /// C2 (C# `InitializeAsync` awaits `RemoveLeftovers`): no agy run starts until what a crash left in agy's settings is out; another
    /// provider's run never waits for it.
    #[tokio::test(flavor = "multi_thread")]
    async fn no_agy_run_starts_before_a_crashs_leftovers_are_out() {
        let folder = folder("leftovers");
        // A launch that crashed holding `command(git clean -fdx)`.
        let crashed = rules(&folder, leases::portable_replace);
        crashed.apply(&AllowGrant::new("agy-0", &["command(git clean -fdx)"], true)).unwrap();
        assert!(std::fs::read_to_string(settings_of(&folder)).unwrap().contains("git clean"), "left behind");
        let launched = rules(&folder, leftovers_swap);
        let (shared, done) = shared(launched.clone(), false);
        remove_leftovers(launched, done);
        until(|| LEFTOVERS.reached.load(Ordering::SeqCst), "the removal under way").await;
        let other = StartRunRequest::new("claude-pane", "ws", "claude", "hi").with_provider("claude");
        shared.hold_antigravity_rules(&other).await.unwrap();
        let waiting = {
            let shared = shared.clone();
            tokio::spawn(async move {
                shared.hold_antigravity_rules(&StartRunRequest::new("pane", "ws", "claude", "hi").with_provider("gemini")).await
            })
        };
        tokio::time::sleep(Duration::from_millis(200)).await;
        assert!(!waiting.is_finished(), "an agy run waits while the leftovers go");
        LEFTOVERS.let_go();
        waiting.await.unwrap().unwrap();
        assert_eq!(std::fs::read_to_string(settings_of(&folder)).unwrap(), SETTINGS, "and starts only once they are out");
        let _ = std::fs::remove_dir_all(&folder);
    }

    /// C10: the end of a pane's run releases only the rules its own start holds — the rules of the start that already took its place (its
    /// route there before its rules) stay until that one ends.
    #[tokio::test(flavor = "multi_thread")]
    async fn a_runs_end_releases_only_its_own_starts_rules() {
        let folder = folder("holder");
        let rules = rules(&folder, leases::portable_replace);
        let (shared, _done) = shared(rules.clone(), true);
        // The next start's route is in place and holds its rules.
        lock(&shared.state).routes.insert("pane".into(), Route { holder: 2, ..Route::default() });
        shared.hold_antigravity_rules(&continuation("pane")).await.unwrap();
        assert_eq!(lock(&shared.antigravity).leases.get("pane").map(|(holder, _)| *holder), Some(2), "held by the start's route");
        // The run before it ends late.
        shared.receive_antigravity(&RunEvent::state("pane", "completed", None));
        rules.settled();
        assert!(lock(&shared.antigravity).leases.contains_key("pane") && rules.held().len() == 1, "the next start's rules stay");
        // Its own run ends.
        lock(&shared.state).routes.remove("pane");
        shared.receive_antigravity(&RunEvent::state("pane", "completed", None));
        until(|| rules.held().is_empty(), "the rules released").await;
        rules.settled();
        assert!(!lock(&shared.antigravity).leases.contains_key("pane"), "and the lease gone");
        assert_eq!(std::fs::read_to_string(settings_of(&folder)).unwrap(), SETTINGS);
        let _ = std::fs::remove_dir_all(&folder);
    }

    static ABANDONED: Gate = Gate::new();
    fn abandoned_swap(temporary: &Path, target: &Path) -> std::io::Result<()> {
        ABANDONED.pass();
        leases::portable_replace(temporary, target)
    }

    /// L6: a start abandoned while its rules go in (its future dropped) does not lose their lease: the blocking work that put them in keeps
    /// it on the desk, where the run's end, the next hold or quit releases it.
    #[tokio::test(flavor = "multi_thread")]
    async fn a_start_abandoned_while_its_rules_go_in_keeps_their_lease() {
        let folder = folder("abandoned");
        let rules = rules(&folder, abandoned_swap);
        let (shared, _done) = shared(rules.clone(), true);
        lock(&shared.state).routes.insert("pane".into(), Route { holder: 7, ..Route::default() });
        let hold = {
            let shared = shared.clone();
            tokio::spawn(async move { shared.hold_antigravity_rules(&continuation("pane")).await })
        };
        until(|| ABANDONED.reached.load(Ordering::SeqCst), "the rules going in").await;
        hold.abort();
        assert!(hold.await.unwrap_err().is_cancelled(), "the start is abandoned");
        ABANDONED.let_go();
        until(|| lock(&shared.antigravity).leases.contains_key("pane"), "the lease kept").await;
        assert_eq!(rules.held().get("command(echo hi)"), Some(&1), "held once");
        // Released as any: by its run's end.
        lock(&shared.state).routes.remove("pane");
        shared.receive_antigravity(&RunEvent::state("pane", "stopped", None));
        until(|| rules.held().is_empty(), "released").await;
        rules.settled();
        assert_eq!(std::fs::read_to_string(settings_of(&folder)).unwrap(), SETTINGS);
        let _ = std::fs::remove_dir_all(&folder);
    }
}
