//! CLI accounts and login recovery, `loginAction` (C# MainWindow.LoginRecovery.cs, MainWindow.WebShell.Cards.cs:554-596,
//! MainWindow.Settings.cs:628-1026). Owner: W6-C.
//!
//! The window's accounts (mighty-accounts' `CliAccountsCoordinator` over the host's processes, the run engine's catalog and the
//! terminal window port) and its login recovery: a run that lost its sign-in leaves its request in the retry book; once the account's
//! status confirms it, the pane's sign-in card shows, and — with automatic sign-in on — the provider's sign-in starts by itself (one at
//! a time, a cooldown after each); once signed in again, every waiting request of that provider is sent again, once.
//!
//! Machine changes: a sign-in (hidden or in a terminal), a sign-out, the Bedrock reset and the automatic sign-in run only when the app
//! may change the machine (`Host::may_change_machine`: `--allow-state-write`, never in a smoke); otherwise the row or the card says
//! "verification mode" and nothing runs. A smoke reads no account status (no CLI is asked).
//!
//! Locks: the state's lock may be held while an account lock is taken (`login_input`, `busy`, `prune` run with the state locked), so
//! no account lock is ever held across `host.view()`, `redraw()` or another part's call: each is taken for one map operation through
//! the `Accounts` helpers and let go before anything else runs.

use std::collections::{HashMap, HashSet};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::Duration;

use mighty_accounts::accounts::{
    CliAccountStatus, CliAccountsCoordinator, CliLoginOption, HostRunner, SECTION_PROVIDERS, login_arguments, login_hint,
};
use mighty_accounts::login::{self, CliAutoLoginGate, CliBackgroundLogin, CliLoginRetry, CliLoginRetryBook};
use mighty_accounts::terminal::{CliLoginOutcome, CliTerminalSignIn, login_wait, terminal_login};
use mighty_composer::cards::{LoginInput, LoginJob};
use mighty_core::model::{AppSnapshot, RunSession};
use mighty_native::surface::DialogResult;
use mighty_run::{RunEvent, StartRunRequest, strings};
use serde_json::Value;
use tauri::Manager as _;
use tokio::sync::watch;

use crate::host::{Host, Reply, lock};
use crate::methods::text;

/// The user's home folder (C# `Environment.SpecialFolder.UserProfile`): the CLIs' commands start there.
pub fn home() -> String {
    std::env::var("USERPROFILE").or_else(|_| std::env::var("HOME")).unwrap_or_default()
}

/// A sign-in under way (C# `LoginJob`).
struct Job {
    serial: u64,
    automatic: bool,
    terminal: bool,
    phase: String,
    process: Option<Arc<CliBackgroundLogin>>,
    /// Ends the job's task (C#'s cancellation).
    cancel: watch::Sender<bool>,
}

/// A terminal window a terminal sign-in opened: whether it holds the command (the console host), and whether it closed.
#[derive(Clone)]
struct LoginWindow {
    holds_command: bool,
    closed: Arc<AtomicBool>,
}

impl LoginWindow {
    /// The wait's "still open" (C# `!(window.ClosesWithWindow && window.Exit.IsCompleted)`): a Windows Terminal tab handed its command
    /// off, so only the console host's closing is known.
    fn open(&self) -> bool {
        !(self.holds_command && self.closed.load(Ordering::SeqCst))
    }

    /// Whether a new start of the provider waits on this window again instead of opening another (C# `{ Open: true }`:
    /// `ClosesWithWindow && !Exit.IsCompleted`): only a console host window still open; a Windows Terminal tab never.
    fn reusable(&self) -> bool {
        self.holds_command && !self.closed.load(Ordering::SeqCst)
    }
}

/// The window's accounts and login recovery (C#'s `accountsCoordinator`, `loginRetries`, `loginJobs`, `loginBusy`, `loginFailures`,
/// `loginNotes`, `accountChanges`, `loginResendsAwaitingUpdate`, `autoLogin`, `deferredAutoLogins`, `loginWindows`,
/// `accountMessages`, `accountLoginWaits`, `accountRowsWorking`, `settingsAccountRefreshes`, `claudeModelReset`).
#[derive(Default)]
pub struct Accounts {
    coordinator: OnceLock<Arc<CliAccountsCoordinator>>,
    book: Mutex<CliLoginRetryBook>,
    gate: Mutex<CliAutoLoginGate>,
    jobs: Mutex<HashMap<String, Job>>,
    busy: Mutex<HashSet<String>>,
    failures: Mutex<HashMap<String, String>>,
    notes: Mutex<HashMap<String, String>>,
    changes: Mutex<HashSet<String>>,
    awaiting_update: Mutex<HashSet<String>>,
    deferred: Mutex<HashMap<String, (CliLoginRetry, CliAccountStatus)>>,
    windows: Mutex<HashMap<String, LoginWindow>>,
    messages: Mutex<HashMap<String, String>>,
    /// Each terminal sign-in waited on, with what ends its wait.
    waits: Mutex<HashMap<String, watch::Sender<bool>>>,
    working: Mutex<HashSet<String>>,
    reading: AtomicUsize,
    model_reset: AtomicBool,
    checking_runtime: AtomicBool,
    serial: AtomicU64,
    /// Sign-outs confirmed in D8, counted before any gate (the smoke's check that D8 cancelled confirmed none).
    sign_out_attempts: AtomicUsize,
    /// The app is closing: no sign-in starts, every wait ends.
    closed: AtomicBool,
}

impl Accounts {
    fn has_job(&self, provider: &str) -> bool {
        lock(&self.jobs).contains_key(provider)
    }
    fn is_busy(&self, provider: &str) -> bool {
        lock(&self.busy).contains(provider)
    }
    fn is_changing(&self, provider: &str) -> bool {
        lock(&self.changes).contains(provider)
    }
    fn is_waiting(&self, provider: &str) -> bool {
        lock(&self.waits).contains_key(provider)
    }
    fn is_current(&self, retry: &CliLoginRetry) -> bool {
        lock(&self.book).is_current(retry)
    }
    fn has_deferred(&self, provider: &str) -> bool {
        lock(&self.deferred).contains_key(provider)
    }
    /// The job of `provider` when it is still the one numbered `serial`.
    fn is_job(&self, provider: &str, serial: u64) -> bool {
        lock(&self.jobs).get(provider).is_some_and(|job| job.serial == serial)
    }
    /// The job `serial` of `provider` changed by `change`, when it is still that job.
    fn with_job(&self, provider: &str, serial: u64, change: impl FnOnce(&mut Job)) {
        if let Some(job) = lock(&self.jobs).get_mut(provider).filter(|job| job.serial == serial) {
            change(job);
        }
    }
}

/// The window's accounts, made on first use.
pub fn of(host: &Host) -> Arc<Accounts> {
    if let Some(accounts) = host.app.try_state::<Arc<Accounts>>() {
        return accounts.inner().clone();
    }
    host.app.manage(Arc::new(Accounts::default()));
    host.app.state::<Arc<Accounts>>().inner().clone()
}

/// The coordinator, once the run engine (its catalog, its CLI environment) is there.
fn coordinator(host: &Host) -> Option<Arc<CliAccountsCoordinator>> {
    let accounts = of(host);
    if let Some(made) = accounts.coordinator.get() {
        return Some(made.clone());
    }
    let catalog = host.runs()?.catalog.clone();
    // Made once the ports are managed too, so its sign-ins have their terminal window.
    let terminals = host.app.try_state::<crate::platform::Ports>()?.terminals.clone();
    let made = CliAccountsCoordinator::new(Arc::new(HostRunner::new(host.processes.clone())), catalog, home()).with_terminal(terminals);
    let _ = accounts.coordinator.set(Arc::new(made));
    accounts.coordinator.get().cloned()
}

/// A provider's status read now (none in a smoke, or before the run engine is there).
async fn read_status(host: &Host, provider: &str) -> Option<CliAccountStatus> {
    if host.smoke.is_some() {
        return None;
    }
    Some(coordinator(host)?.status(provider).await)
}

/// The panes' cards and the CLI tab drawn again.
fn redraw(host: &Host) {
    crate::panes::mark_all(host);
    if crate::native::is_open(host, crate::settings::SURFACE) {
        crate::native::touch(host, crate::settings::SURFACE);
    }
}

/// "Verification mode": what a machine change says when the app may not make it.
fn verification() -> String {
    strings::get("windows.notifications.statusVerificationMode")
}

/// `HasActiveProvider(provider)`.
fn active_provider(host: &Host, provider: &str) -> bool {
    host.runs().is_some_and(|r| r.service.has_active_provider(provider))
}

fn session_running(host: &Host, id: &str) -> bool {
    host.runs().is_some_and(|r| r.service.is_session_running(id))
}

// ── the seams ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// `accounts::statuses`: each CLI's account status as last read (C# `cliAccountStatuses`), for the setup screen's sign-in row.
#[allow(dead_code, reason = "a seam W6-B (the setup screen's sign-in row) calls")]
pub fn statuses(host: &Host) -> Vec<CliAccountStatus> {
    let Some(coordinator) = of(host).coordinator.get().cloned() else { return vec![] };
    let read = coordinator.statuses();
    SECTION_PROVIDERS.iter().filter_map(|p| read.get(*p).cloned()).collect()
}

/// `accounts::busy`: whether the provider's sign-in, sign-out or terminal wait is under way (C# `loginBusy`, `accountChanges`,
/// `accountLoginWaits`), for the update holds' `ProviderUse` and `RequireIdleAccount`. With the state locked: the accounts' own state
/// only, never `host.view()`.
#[allow(dead_code, reason = "a seam W6-D (the update holds' provider use) calls")]
pub fn busy(host: &Host, provider: &str) -> bool {
    let accounts = of(host);
    accounts.is_busy(provider) || accounts.is_changing(provider) || accounts.is_waiting(provider)
}

/// `accounts::start_sign_in`: C# `StartCliSignIn(provider, option)` — the CLI's own sign-in in a terminal window, then its status
/// polled until it reads signed in. `option`: `console`, `bedrock`, else the account.
#[allow(dead_code, reason = "a seam for the panes; the setup screen awaits start_cli_sign_in to say its refusal")]
pub fn start_sign_in(host: &Arc<Host>, provider: &str, option: Option<&str>) {
    let option = match option {
        Some("console") => CliLoginOption::Console,
        Some("bedrock") => CliLoginOption::Bedrock,
        _ => CliLoginOption::Account,
    };
    let (host, provider) = (host.clone(), provider.to_string());
    crate::native::act(&host.clone(), async move { start_cli_sign_in(&host, &provider, option).await });
}

/// `accounts::refresh`: C# `RefreshVisibleSettingsAccountsAsync` — every account status read again, the CLI tab's rings turning
/// meanwhile (Settings' CLI tab as it opens, outside a smoke).
pub fn refresh(host: &Arc<Host>) {
    if host.smoke.is_some() {
        return;
    }
    let host = host.clone();
    tauri::async_runtime::spawn(async move {
        let accounts = of(&host);
        accounts.reading.fetch_add(1, Ordering::SeqCst);
        redraw(&host);
        refresh_cli_accounts(&host).await;
        accounts.reading.fetch_sub(1, Ordering::SeqCst);
        redraw(&host);
    });
}

/// `accounts::resend_after_update`: C# `ResendLoginRequestsAfterUpdate` — the requests held while a CLI update ran, sent once it
/// ended (W6-D calls it).
#[allow(dead_code, reason = "a seam W6-D (after an update) calls")]
pub fn resend_after_update(host: &Arc<Host>) {
    let host = host.clone();
    tauri::async_runtime::spawn(async move {
        if host.closing() || crate::updates::any_running(&host) || crate::plugins::operations_running(&host) {
            return;
        }
        let mut waiting: Vec<String> = lock(&of(&host).awaiting_update).iter().cloned().collect();
        waiting.sort();
        for id in waiting {
            lock(&of(&host).awaiting_update).remove(&id);
            resend_login_request(&host, &id).await;
        }
    });
}

/// `accounts::login_input`: what the pane's sign-in card shows (C# `WebLogin`'s state). With the state locked: the accounts' own state
/// and `snapshot`, never `host.view()`.
pub fn login_input(host: &Host, _snapshot: &AppSnapshot, pane: &RunSession) -> Option<LoginInput> {
    let accounts = of(host);
    let retry = lock(&accounts.book).requests().get(pane.id()).cloned()?;
    let provider = retry.request.provider.clone();
    if pane.provider() != provider {
        return None;
    }
    let job = lock(&accounts.jobs).get(&provider).map(|job| {
        let output = job.process.as_ref().map(|p| p.output()).unwrap_or_default();
        LoginJob {
            automatic: job.automatic,
            phase: job.phase.clone(),
            terminal: job.terminal,
            url: output.url.map(|u| u.to_string()),
            asks_for_code: output.asks_for_code,
        }
    });
    Some(LoginInput {
        failure: lock(&accounts.failures).get(&provider).cloned(),
        note: lock(&accounts.notes).get(pane.id()).cloned(),
        account_wait: lock(&accounts.waits).contains_key(&provider),
        job,
        provider,
    })
}

/// `loginAction{paneId, action, code?}` (C# `WebLoginAction`): the sign-in card's buttons.
pub async fn login_action(host: Arc<Host>, args: Value) -> Reply {
    let Some(pane) = text(&args, "paneId").map(str::to_string) else { return Ok(Value::Null) };
    let accounts = of(&host);
    let Some(retry) = lock(&accounts.book).requests().get(&pane).cloned() else { return Ok(Value::Null) };
    let provider = retry.request.provider.clone();
    match text(&args, "action").unwrap_or("") {
        "login" => start_background_login(&host, &provider, false, Some(&pane)),
        "cancel" => {
            cancel_background_login(&host, &provider);
            redraw(&host);
        }
        "terminal" => terminal_login_fallback(&host, &provider).await,
        "resend" => resend_login_request(&host, &pane).await,
        "dismiss" => dismiss(&host, &pane),
        "openLink" => {
            let url = lock(&accounts.jobs).get(&provider).and_then(|j| j.process.as_ref().and_then(|p| p.output().url));
            if let Some(url) = url {
                crate::platform::open_url(url.as_str())?;
            }
        }
        "code" => {
            let process = lock(&accounts.jobs).get(&provider).and_then(|j| j.process.clone());
            if let Some(process) = process {
                process.send_code(text(&args, "code").unwrap_or(""));
            }
        }
        _ => return Err("Unknown action.".into()),
    }
    Ok(Value::Null)
}

/// A run event (C# `ReceiveLoginSignal`, before the pane takes the event): a run that ended with the sign-in reason is confirmed with
/// the account's status; a provider's deferred automatic start is tried again once its run ended.
pub fn on_run_event(host: &Arc<Host>, event: &RunEvent) {
    let Some(status) = event.status.as_deref().filter(|s| matches!(*s, "completed" | "stopped" | "error")) else { return };
    if event.kind != "status" {
        return;
    }
    let accounts = of(host);
    let retry = lock(&accounts.book).settled(&event.session_id);
    let (provider, remote) = {
        let view = host.view();
        let pane = view.snapshot.session(&event.session_id).cloned();
        let remote = pane.as_ref().and_then(|p| view.snapshot.workspace(p.workspace_id())).is_some_and(|w| w.remote.is_some());
        (pane.map(|p| p.provider().to_string()), remote)
    };
    // A remote run signs in on its host: this PC's sign-in did not fail.
    if status == "error"
        && event.reason.as_deref() == Some("authentication")
        && let Some(retry) = retry
        && !remote
    {
        let host = host.clone();
        tauri::async_runtime::spawn(async move { confirm_login_failure(&host, retry).await });
    }
    if let Some(provider) = provider
        && accounts.has_deferred(&provider)
    {
        let host = host.clone();
        tauri::async_runtime::spawn(async move { resume_deferred_auto_login(&host, &provider).await });
    }
}

/// A request about to start (C# `service.RequestStarting`, MainWindow.LoginRecovery.cs:49): its generation in the retry book, its
/// note and held resend forgotten, a sign-in of its provider no pane waits on any more cancelled.
pub fn on_request_starting(host: &Arc<Host>, request: &StartRunRequest) {
    let accounts = of(host);
    lock(&accounts.book).sent(request, None);
    lock(&accounts.notes).remove(&request.session_id);
    lock(&accounts.awaiting_update).remove(&request.session_id);
    cancel_unused_login(host, &request.provider);
    redraw(host);
}

// ── login recovery ────────────────────────────────────────────────────────────────────────────────────────────────────────

/// `ConfirmLoginFailure`: the account's status read; once the pane is idle (at most 10 s), a status a sign-in can fix keeps the request
/// for its retry and may start the automatic sign-in.
async fn confirm_login_failure(host: &Arc<Host>, retry: CliLoginRetry) {
    let Some(status) = read_status(host, &retry.request.provider).await else { return };
    let accounts = of(host);
    let id = retry.request.session_id.clone();
    for _ in 0..200 {
        if !accounts.is_current(&retry) || !session_running(host, &id) || host.closing() {
            break;
        }
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    if host.closing() || !accounts.is_current(&retry) || !login::sign_in_can_fix(&status) {
        return;
    }
    let pane = host.view().snapshot.session(&id).cloned();
    if pane.is_none_or(|p| p.provider() != retry.request.provider || p.status() == "running") || session_running(host, &id) {
        return;
    }
    lock(&accounts.book).remember(retry.clone());
    redraw(host);
    start_automatic_login_if_allowed(host, retry, status);
}

/// `StartAutomaticLoginIfAllowed`: one sign-in per provider however many panes lost it together, none for a while after one ended,
/// none while the provider runs (deferred until it is idle), and only with the switch on and the app allowed to change the machine.
fn start_automatic_login_if_allowed(host: &Arc<Host>, retry: CliLoginRetry, status: CliAccountStatus) {
    let accounts = of(host);
    let provider = retry.request.provider.clone();
    let changing = accounts.has_job(&provider)
        || accounts.is_busy(&provider)
        || accounts.is_changing(&provider)
        || accounts.is_waiting(&provider)
        || crate::updates::any_running(host);
    let enabled = host.view().snapshot.auto_login_clis != Some(false) && host.may_change_machine();
    let now = mighty_core::time::clock_now();
    let allowed = |active: bool| lock(&accounts.gate).should_start(&provider, enabled, &status, active, now, retry.resent, retry.sent_at);
    if host.closing() {
        return;
    }
    if !changing && active_provider(host, &provider) {
        if allowed(false) {
            lock(&accounts.deferred).insert(provider, (retry, status));
        }
        return;
    }
    if !allowed(changing) {
        return;
    }
    lock(&accounts.deferred).remove(&provider);
    start_background_login(host, &provider, true, Some(&retry.request.session_id));
}

/// `ResumeDeferredAutoLogin`: once nothing of the provider runs (at most 10 s), the held automatic start is tried again while its pane
/// still waits with the same request.
async fn resume_deferred_auto_login(host: &Arc<Host>, provider: &str) {
    for _ in 0..200 {
        if host.closing() || !active_provider(host, provider) {
            break;
        }
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    if host.closing() || active_provider(host, provider) {
        return;
    }
    let accounts = of(host);
    let Some((retry, status)) = lock(&accounts.deferred).remove(provider) else { return };
    let waiting = lock(&accounts.book).requests().get(&retry.request.session_id) == Some(&retry);
    if waiting {
        start_automatic_login_if_allowed(host, retry, status);
    }
}

/// `StartBackgroundLogin`: the provider's sign-in — hidden in a pseudo terminal, or for a terminal sign-in (Antigravity, pi,
/// opencode, Hermes) in a terminal window. Refused (the card says why) while the account changes, a CLI updates or the provider runs;
/// in a smoke nothing starts; without `--allow-state-write` the card says "verification mode".
fn start_background_login(host: &Arc<Host>, provider: &str, automatic: bool, session: Option<&str>) {
    let accounts = of(host);
    let closing = host.closing() || accounts.closed.load(Ordering::SeqCst);
    if closing || accounts.has_job(provider) || !login::PROVIDERS.contains(&provider) || host.smoke.is_some() {
        return;
    }
    if !host.may_change_machine() {
        lock(&accounts.failures).insert(provider.to_string(), verification());
        redraw(host);
        return;
    }
    let updating = crate::updates::any_running(host);
    let held = accounts.is_busy(provider) || accounts.is_changing(provider) || accounts.is_waiting(provider);
    if held || updating || active_provider(host, provider) {
        let key = if updating { "loginRecovery.updating" } else { "loginRecovery.busy" };
        lock(&accounts.failures).insert(provider.to_string(), strings::get(key));
        redraw(host);
        return;
    }
    let terminal = CliTerminalSignIn::get(provider).copied();
    let serial = accounts.serial.fetch_add(1, Ordering::SeqCst) + 1;
    let (cancel, cancelled) = watch::channel(false);
    lock(&accounts.jobs).insert(
        provider.to_string(),
        Job { serial, automatic, terminal: terminal.is_some(), phase: "starting".into(), process: None, cancel },
    );
    lock(&accounts.busy).insert(provider.to_string());
    lock(&accounts.failures).remove(provider);
    redraw(host);
    let (host, provider, session) = (host.clone(), provider.to_string(), session.map(str::to_string));
    tauri::async_runtime::spawn(async move {
        let mut stop = cancelled.clone();
        // The hidden terminal, closed below whatever ends the job, before the provider is let go (C# keeps `loginBusy` until
        // `DisposeAsync`).
        let started: Started = Arc::default();
        let work = async {
            match terminal {
                Some(entry) => run_terminal_sign_in(&host, entry, serial, session.as_deref()).await,
                None => run_background_login(&host, &provider, serial, started.clone()).await,
            }
        };
        let ended = tokio::select! {
            outcome = work => Some(outcome),
            _ = stop.wait_for(|stopped| *stopped) => None,
        };
        let process = lock(&started).take();
        if let Some(process) = process {
            process.close().await;
        }
        let accounts = of(&host);
        let current = accounts.is_job(&provider, serial);
        match ended {
            Some(Ok(true)) if current => login_succeeded(&host, &provider).await,
            Some(Err(failure)) if current && !host.closing() => {
                lock(&accounts.jobs).remove(&provider);
                lock(&accounts.gate).stopped(&provider, mighty_core::time::clock_now());
                lock(&accounts.failures).insert(provider.clone(), failure);
            }
            _ => {}
        }
        if accounts.is_job(&provider, serial) {
            lock(&accounts.jobs).remove(&provider);
        }
        lock(&accounts.busy).remove(&provider);
        redraw(&host);
    });
}

/// The hidden sign-in a job started, for its task to close whatever ended it (C#'s `finally { await job.Process.DisposeAsync() }`).
type Started = Arc<Mutex<Option<Arc<CliBackgroundLogin>>>>;

/// `RunBackgroundLogin`: the status read again (a Bedrock or API-key configuration chosen meanwhile never starts a browser sign-in),
/// the CLI's own sign-in in a hidden pseudo terminal in the home, its link and code prompt shown on the card, then the wait
/// (`CliLoginWait`). Ok(true) signed in; Err the card's failure.
async fn run_background_login(host: &Arc<Host>, provider: &str, serial: u64, started: Started) -> Result<bool, String> {
    let failed = || strings::get("loginRecovery.startFailed");
    let status = read_status(host, provider).await.ok_or_else(failed)?;
    if !login::sign_in_can_fix(&status) {
        return Err(failed());
    }
    let runs = host.runs().cloned().ok_or_else(failed)?;
    let command = runs.catalog.find(provider).await.ok_or_else(failed)?;
    let argv = login_arguments(provider, CliLoginOption::Account).ok_or_else(failed)?;
    // The hidden terminal is the platform's PtyHost, which the app hands the accounts (W6-A's main.rs manages it).
    let ptys = host.app.try_state::<Arc<dyn mighty_platform::PtyHost>>().map(|p| p.inner().clone()).ok_or_else(failed)?;
    let arguments: Vec<String> = argv[1..].iter().map(|a| a.to_string()).collect();
    let environment = runs.catalog.environment().current().values;
    let process = Arc::new(
        CliBackgroundLogin::start(ptys.as_ref(), &command, &arguments, &PathBuf::from(home()), environment).await.map_err(|_| failed())?,
    );
    *lock(&started) = Some(process.clone());
    let accounts = of(host);
    accounts.with_job(provider, serial, |job| {
        job.process = Some(process.clone());
        job.phase = "waiting".into();
    });
    redraw(host);
    // Each new reading of the output redraws the card.
    let mut changes = process.changes();
    let watcher = host.clone();
    let watching = tauri::async_runtime::spawn(async move {
        while changes.changed().await.is_ok() {
            redraw(&watcher);
        }
    });
    let reader = host.clone();
    let id = provider.to_string();
    let outcome = login_wait(
        || process.is_running(),
        || process.exit_code(),
        || {
            let (reader, id) = (reader.clone(), id.clone());
            async move { read_status(&reader, &id).await.unwrap_or_else(|| CliAccountStatus::new(&id)) }
        },
        None,
        None,
        None,
    )
    .await;
    watching.abort();
    process.finish_output();
    match outcome.outcome {
        CliLoginOutcome::LoggedIn => {
            // The CLI finishes saving its credential files before a lingering job ends, then its terminal closes.
            let _ = tokio::time::timeout(Duration::from_secs(15), process.completion()).await;
            let taken = lock(&started).take();
            if let Some(process) = taken {
                process.close().await;
            }
            Ok(true)
        }
        CliLoginOutcome::TimedOut => Err(strings::get("loginRecovery.timedOut")),
        CliLoginOutcome::Exited => Err(strings::get("loginRecovery.exited")),
    }
}

/// `RunTerminalSignIn`: a terminal sign-in (Antigravity, pi, opencode, Hermes) opened only for a status that reads signed out, in
/// the failed pane's workspace (never a remote one), titled as the table says; a window still open is waited on again instead of
/// opening another; signed in once the provider's status says so (`CliTerminalSignIn.WaitAsync`).
async fn run_terminal_sign_in(host: &Arc<Host>, entry: CliTerminalSignIn, serial: u64, session: Option<&str>) -> Result<bool, String> {
    let provider = entry.provider;
    let failed = || strings::get("loginRecovery.startFailed");
    let status = read_status(host, provider).await.ok_or_else(failed)?;
    if !login::sign_in_can_fix(&status) {
        return Err(failed());
    }
    let argv: Vec<String> = entry.command().ok_or_else(failed)?.iter().map(|a| a.to_string()).collect();
    let directory = {
        let view = host.view();
        let state = &view.snapshot;
        let workspace =
            session.and_then(|id| state.session(id)).map(|p| p.workspace_id().to_string()).or_else(|| state.active_workspace_id.clone());
        workspace.and_then(|id| state.workspace(&id).filter(|w| w.remote.is_none()).map(|w| w.path().to_string()))
    };
    let accounts = of(host);
    let kept = lock(&accounts.windows).get(provider).filter(|w| w.reusable()).cloned();
    let window = match kept {
        Some(window) => window,
        None => {
            let coordinator = coordinator(host).ok_or_else(failed)?;
            let opened = coordinator.start_sign_in(&argv, directory.as_deref(), Some(&entry.title())).await.map_err(|_| failed())?;
            let window = LoginWindow { holds_command: opened.closes_with_window, closed: Arc::new(AtomicBool::new(false)) };
            let closed = window.closed.clone();
            // Not the job's: the window stays the user's, and closing the app never waits for it.
            tauri::async_runtime::spawn(async move {
                let _ = opened.window.wait().await;
                closed.store(true, Ordering::SeqCst);
            });
            lock(&accounts.windows).insert(provider.to_string(), window.clone());
            window
        }
    };
    accounts.with_job(provider, serial, |job| job.phase = "waiting".into());
    redraw(host);
    let reader = host.clone();
    let outcome = CliTerminalSignIn::wait(
        provider,
        || {
            let reader = reader.clone();
            async move { read_status(&reader, provider).await.unwrap_or_else(|| CliAccountStatus::new(provider)) }
        },
        || window.open(),
        None,
        None,
        None,
    )
    .await;
    match outcome.outcome {
        CliLoginOutcome::LoggedIn => Ok(true),
        CliLoginOutcome::TimedOut => Err(strings::get("loginRecovery.timedOut")),
        CliLoginOutcome::Exited => Err(strings::get("loginRecovery.exited")),
    }
}

/// `LoginSucceeded`: automatic starts held for the cooldown, the provider's model lists marked old, the accounts read again, then
/// each request of the provider that lost the sign-in sent again.
async fn login_succeeded(host: &Arc<Host>, provider: &str) {
    let accounts = of(host);
    lock(&accounts.gate).succeeded(provider, mighty_core::time::clock_now());
    lock(&accounts.jobs).remove(provider);
    lock(&accounts.failures).remove(provider);
    lock(&accounts.busy).remove(provider);
    crate::models::after_account_change(host, provider);
    refresh_cli_accounts(host).await;
    if host.closing() {
        return;
    }
    resend_waiting_login_requests(host, provider).await;
}

/// `ResendWaitingLoginRequests`.
async fn resend_waiting_login_requests(host: &Arc<Host>, provider: &str) {
    let ids: Vec<String> =
        lock(&of(host).book).requests().values().filter(|r| r.request.provider == provider).map(|r| r.request.session_id.clone()).collect();
    for id in ids {
        resend_login_request(host, &id).await;
    }
}

/// `ResendLoginRequest`: the pane's waiting request sent again (its words and attachments, the pane's model and settings now), marked
/// as login recovery's own resend; held with a note while a CLI updates or the account changes; a pane that runs or is gone drops it.
async fn resend_login_request(host: &Arc<Host>, id: &str) {
    let accounts = of(host);
    let Some(retry) = lock(&accounts.book).requests().get(id).cloned() else { return };
    if host.closing() {
        return;
    }
    let pane = host.view().snapshot.session(id).cloned();
    let Some(pane) = pane.filter(|p| p.provider() == retry.request.provider && p.status() != "running" && !session_running(host, id))
    else {
        dismiss(host, id);
        return;
    };
    let block = crate::updates::manual_block(host, &host.view().snapshot, &pane);
    let updating = crate::updates::any_running(host);
    let held = accounts.is_busy(pane.provider()) || accounts.is_changing(pane.provider());
    if updating || block.is_some() || held {
        if updating || block.is_some() {
            lock(&accounts.awaiting_update).insert(id.to_string());
        }
        let note = block.unwrap_or_else(|| strings::get(if updating { "loginRecovery.updating" } else { "loginRecovery.busy" }));
        lock(&accounts.notes).insert(id.to_string(), note);
        redraw(host);
        return;
    }
    lock(&accounts.awaiting_update).remove(id);
    let outcome = start_again(host, id, &retry).await;
    if let Err(error) = outcome {
        let kept = lock(&accounts.book).remember(retry);
        if kept && let Some(error) = error {
            lock(&accounts.notes).insert(id.to_string(), error);
        }
    }
    redraw(host);
}

/// Ends the resend's claim on the pane's composer (C#'s `finally { starting = false; }`).
struct Starting<'a>(&'a Host, &'a str);

impl Drop for Starting<'_> {
    fn drop(&mut self) {
        if let Ok(ui) = crate::panes::ui(&mut self.0.view(), self.1) {
            ui.view.starting = false;
            ui.dirty = true;
        }
    }
}

/// `PaneView.ResendLoginRequest`: not while the pane's composer starts a request or a queued one, or the pane runs; the composer held
/// meanwhile (`starting`) so neither it nor its queue starts another; the models made ready, the provider's runtime available, then the
/// request as the pane would send it now (an Antigravity model its live list lacks giving way to agy's default, as
/// `PrepareStyleRunRequest` does), through the run service. Its own sign-in failure only raises the card again (never an automatic
/// start). Err(None): not now, nothing to say; Err(Some(why)): the note the card shows.
async fn start_again(host: &Arc<Host>, id: &str, retry: &CliLoginRetry) -> Result<(), Option<String>> {
    let refused = || Some(strings::get("loginRecovery.resendFailed"));
    let engine = host.engine().map_err(Some)?;
    let submission = {
        let mut view = host.view();
        let running = view.snapshot.session(id).is_none_or(|p| p.status() == "running");
        let ui = crate::panes::ui(&mut view, id).map_err(|_| None::<String>)?;
        if running || ui.view.starting || ui.view.queue_starting {
            return Err(None);
        }
        ui.view.starting = true;
        ui.submission += 1;
        ui.dirty = true;
        ui.submission
    };
    let _starting = Starting(host, id);
    let accounts = of(host);
    if session_running(host, id) || !accounts.is_current(retry) {
        return Err(None);
    }
    crate::models::prepare(host, id).await;
    let provider = retry.request.provider.clone();
    let runtime = host.runs().and_then(|runs| runs.runtime(&provider));
    if runtime.as_ref().is_none_or(|r| !r.available) {
        return Err(Some(strings::get("composer.hint.refreshRuntime")));
    }
    let request = {
        let mut view = host.view();
        let pane = view.snapshot.session(id).cloned().ok_or_else(refused)?;
        let current = crate::panes::ui(&mut view, id).is_ok_and(|ui| ui.submission == submission);
        if !current || pane.provider() != provider || host.closing() {
            return Err(None);
        }
        let workspace = view.snapshot.workspace(pane.workspace_id()).cloned();
        let mut request = StartRunRequest::new(pane.id(), pane.workspace_id(), pane.kind(), &retry.request.input);
        request.registered_models = crate::runs::registered_models_for(pane.provider(), workspace.as_ref(), &view.snapshot);
        request.model = pane.model().to_string();
        if pane.provider() == "gemini"
            && runtime.as_ref().is_some_and(|r| mighty_run::antigravity::model_unavailable(pane.model(), &r.model_catalog))
        {
            request.model = "default".into();
        }
        request.provider = provider.clone();
        request.settings = pane.settings.clone();
        request.resume_id = pane.resume_id.clone();
        request.attachments = retry.request.attachments.clone();
        request
    };
    if !accounts.is_current(retry) {
        return Err(None);
    }
    lock(&accounts.book).expect_resend(id);
    match engine.service.start(request).await {
        Ok(Some(())) => Ok(()),
        Ok(None) => {
            lock(&accounts.book).cancel_resend(id);
            Err(None)
        }
        Err(error) => {
            lock(&accounts.book).cancel_resend(id);
            Err(Some(error.message().to_string()))
        }
    }
}

/// `DismissLoginRecovery`.
fn dismiss(host: &Arc<Host>, id: &str) {
    let accounts = of(host);
    let provider = lock(&accounts.book).requests().get(id).map(|r| r.request.provider.clone());
    lock(&accounts.book).drop_session(id);
    lock(&accounts.notes).remove(id);
    lock(&accounts.awaiting_update).remove(id);
    if let Some(provider) = provider {
        cancel_unused_login(host, &provider);
    }
    redraw(host);
}

/// `CancelUnusedLogin`: a sign-in no pane waits on any more ends.
fn cancel_unused_login(host: &Host, provider: &str) {
    cancel_unused(&of(host), provider);
}

fn cancel_unused(accounts: &Accounts, provider: &str) {
    let used = lock(&accounts.book).requests().values().any(|r| r.request.provider == provider);
    if !used {
        cancel_job(accounts, provider);
    }
}

/// `CancelBackgroundLogin`: the provider's sign-in ended (its automatic starts wait out the cooldown), its failure forgotten.
fn cancel_background_login(host: &Host, provider: &str) {
    cancel_job(&of(host), provider);
}

fn cancel_job(accounts: &Accounts, provider: &str) {
    let job = lock(&accounts.jobs).remove(provider);
    if let Some(job) = job {
        job.cancel.send_replace(true);
        lock(&accounts.gate).stopped(provider, mighty_core::time::clock_now());
    }
    lock(&accounts.failures).remove(provider);
}

/// `TerminalLoginFallback`: the hidden sign-in ended, then the CLI's own sign-in in a terminal window.
async fn terminal_login_fallback(host: &Arc<Host>, provider: &str) {
    cancel_background_login(host, provider);
    for _ in 0..100 {
        if !of(host).is_busy(provider) {
            break;
        }
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    if host.closing() {
        return;
    }
    if let Err(error) = start_cli_sign_in(host, provider, CliLoginOption::Account).await {
        lock(&of(host).failures).insert(provider.to_string(), error);
    }
    redraw(host);
}

// ── the CLI tab's buttons ─────────────────────────────────────────────────────────────────────────────────────────────────

/// `RequireIdleAccount`: refused while the account changes, a CLI updates or the provider runs.
fn require_idle_account(host: &Host, provider: &str) -> Result<(), String> {
    let accounts = of(host);
    if accounts.is_busy(provider) || accounts.is_changing(provider) {
        return Err(strings::get("loginRecovery.busy"));
    }
    if crate::updates::any_running(host) {
        return Err(strings::get("loginRecovery.updating"));
    }
    if active_provider(host, provider) {
        return Err(strings::get("settings.cliAccounts.waitForRuns"));
    }
    Ok(())
}

/// The row's message (`accountMessages`), redrawn.
fn say(host: &Host, provider: &str, message: Option<String>) {
    let accounts = of(host);
    match message {
        Some(message) => lock(&accounts.messages).insert(provider.to_string(), message),
        None => lock(&accounts.messages).remove(provider),
    };
    redraw(host);
}

/// `StartCliSignIn`: the CLI's own sign-in in a terminal window, then the wait (`AwaitTerminalSignIn`); the account is held only
/// while the terminal opens. Bedrock's setup holds it until its window closes, then the status is read once. Its refusal is the
/// caller's to say (the setup screen's message, the row's failure).
pub(crate) async fn start_cli_sign_in(host: &Arc<Host>, provider: &str, option: CliLoginOption) -> Result<(), String> {
    if of(host).closed.load(Ordering::SeqCst) {
        return Ok(());
    }
    require_idle_account(host, provider)?;
    let Some(argv) = login_arguments(provider, option) else { return Ok(()) };
    if !host.may_change_machine() {
        say(host, provider, Some(verification()));
        return Ok(());
    }
    let coordinator = coordinator(host).ok_or_else(|| strings::get("loginRecovery.startFailed"))?;
    say(host, provider, None);
    let accounts = of(host);
    let replaced = lock(&accounts.waits).remove(provider);
    if let Some(replaced) = replaced {
        replaced.send_replace(true);
    }
    let before = coordinator.status_of(provider);
    lock(&accounts.changes).insert(provider.to_string());
    let argv: Vec<String> = argv.iter().map(|a| a.to_string()).collect();
    let opened = coordinator.start_sign_in(&argv, None, None).await;
    let window = match opened {
        Ok(window) => window,
        Err(error) => {
            lock(&accounts.changes).remove(provider);
            refresh_cli_accounts(host).await;
            return Err(error.to_string());
        }
    };
    if option == CliLoginOption::Bedrock {
        let _ = window.window.wait().await;
        lock(&accounts.changes).remove(provider);
        refresh_cli_accounts(host).await;
        note_terminal_sign_in(host, provider);
        return Ok(());
    }
    lock(&accounts.changes).remove(provider);
    if let Some(hint) = login_hint(provider) {
        say(host, provider, Some(hint));
    }
    // Only the console host's window ends with the sign-in's process; a Windows Terminal tab hands off at once.
    let closed = window.closes_with_window.then(|| {
        let (shut, closed) = watch::channel(false);
        tauri::async_runtime::spawn(async move {
            let _ = window.window.wait().await;
            shut.send_replace(true);
        });
        closed
    });
    let (host, provider) = (host.clone(), provider.to_string());
    tauri::async_runtime::spawn(async move { await_terminal_sign_in(&host, &provider, closed, before).await });
    Ok(())
}

/// `AwaitTerminalSignIn`: the account's status read every 5 s for up to ten minutes (`CliTerminalLogin`); "cancel waiting" ends the
/// wait and leaves the terminal to the user; once the account turned from signed out to signed in the waiting requests are resent.
async fn await_terminal_sign_in(host: &Arc<Host>, provider: &str, closed: Option<watch::Receiver<bool>>, before: Option<CliAccountStatus>) {
    let accounts = of(host);
    let (cancel, mut cancelled) = watch::channel(false);
    lock(&accounts.waits).insert(provider.to_string(), cancel.clone());
    redraw(host);
    let reader = host.clone();
    let id = provider.to_string();
    let wait = terminal_login::run(
        || {
            let (reader, id) = (reader.clone(), id.clone());
            async move { read_status(&reader, &id).await.unwrap_or_else(|| CliAccountStatus::new(&id)) }
        },
        closed,
        None,
        None,
        before.as_ref(),
    );
    let result = tokio::select! {
        result = wait => Some(result),
        _ = cancelled.wait_for(|stop| *stop) => None,
    };
    {
        let mut waits = lock(&accounts.waits);
        if waits.get(provider).is_some_and(|w| w.same_channel(&cancel)) {
            waits.remove(provider);
        }
    }
    if host.closing() {
        return;
    }
    if result.as_ref().is_some_and(|r| r.outcome == CliLoginOutcome::LoggedIn) {
        crate::models::after_account_change(host, provider);
    }
    refresh_cli_accounts(host).await;
    redraw(host);
    note_terminal_sign_in(host, provider);
    if result.as_ref().is_some_and(terminal_login::resends) {
        resend_waiting_login_requests(host, provider).await;
    }
}

/// `NoteTerminalSignIn`: a confirmed sign-in from a terminal ends the automatic-start cooldown as a background one does.
fn note_terminal_sign_in(host: &Host, provider: &str) {
    let signed_in = of(host).coordinator.get().and_then(|c| c.status_of(provider)).is_some_and(|s| terminal_login::signed_in(&s));
    if signed_in {
        lock(&of(host).gate).succeeded(provider, mighty_core::time::clock_now());
    }
}

/// `ConfirmCliSignOut` (D8): the sign-out asked; once it took, "계정 변경" signs in again; one that did not take is said in the row.
async fn confirm_cli_sign_out(host: &Arc<Host>, provider: &str, relogin: bool) -> Result<(), String> {
    require_idle_account(host, provider)?;
    say(host, provider, None);
    if crate::native::show_dialog(host, mighty_accounts::view::sign_out_dialog(provider, relogin)).await != DialogResult::Primary {
        return Ok(());
    }
    of(host).sign_out_attempts.fetch_add(1, Ordering::SeqCst);
    require_idle_account(host, provider)?;
    if !host.may_change_machine() {
        say(host, provider, Some(verification()));
        return Ok(());
    }
    let coordinator = coordinator(host).ok_or_else(|| strings::get("loginRecovery.startFailed"))?;
    let accounts = of(host);
    lock(&accounts.changes).insert(provider.to_string());
    let waiting = lock(&accounts.waits).remove(provider);
    if let Some(waiting) = waiting {
        waiting.send_replace(true);
    }
    let after = coordinator.logout(provider).await;
    crate::models::after_account_change(host, provider);
    refresh_cli_accounts(host).await;
    lock(&accounts.changes).remove(provider);
    if after.logged_in == Some(true) {
        say(host, provider, Some(strings::get("settings.cliAccounts.signOutUnconfirmed")));
    } else if relogin {
        start_cli_sign_in(host, provider, CliLoginOption::Account).await?;
    }
    Ok(())
}

/// `RefreshCliAccounts`: every account read again, then the CLIs found again and their lists read for the active workspace.
pub async fn refresh_cli_accounts(host: &Arc<Host>) {
    let Some(coordinator) = coordinator(host).filter(|_| host.smoke.is_none()) else { return };
    coordinator.refresh(&SECTION_PROVIDERS).await;
    crate::models::reload_runtime(host).await;
    redraw(host);
}

/// `ResetClaudeModels`: not while Claude runs (the row says so); every Claude pane back to the default model and effort, the CLIs
/// found again, every local workspace's list read, the row saying how many were read, then the waiting requests resent.
async fn reset_claude_models(host: &Arc<Host>) -> Result<(), String> {
    let accounts = of(host);
    if accounts.model_reset.load(Ordering::SeqCst) {
        return Ok(());
    }
    if active_provider(host, "claude") {
        say(host, "claude", Some(strings::get("settings.cliAccounts.modelReset.running")));
        return Ok(());
    }
    require_idle_account(host, "claude")?;
    accounts.model_reset.store(true, Ordering::SeqCst);
    struct Done(Arc<Accounts>);
    impl Drop for Done {
        fn drop(&mut self) {
            self.0.model_reset.store(false, Ordering::SeqCst);
        }
    }
    let _done = Done(accounts.clone());
    say(host, "claude", Some(strings::get("settings.cliAccounts.modelReset.reloading")));
    host.view().update(|state| *state = mighty_run::model_selection::reset_apply(state))?;
    host.schedule();
    refresh_cli_accounts(host).await;
    let workspaces: Vec<mighty_core::model::Workspace> =
        host.view().snapshot.workspaces().iter().filter(|w| w.remote.is_none() && !w.path().is_empty()).cloned().collect();
    let runs = host.runs().cloned();
    let command = match &runs {
        Some(runs) if host.smoke.is_none() => runs.catalog.find("claude").await,
        _ => None,
    };
    let read: mighty_run::model_selection::ResetRead = Arc::new(move |workspace, _token| {
        let (runs, command) = (runs.clone(), command.clone());
        Box::pin(async move {
            match (runs, command) {
                (Some(runs), Some(command)) => runs.catalog.read_models("claude", &command, Some(workspace.path()), None).await,
                _ => mighty_run::providers::fallback("claude"),
            }
        })
    });
    let said = mighty_run::model_selection::reset_reload(&workspaces, read, mighty_run::model_selection::Cancellation::none(), None)
        .await
        .unwrap_or_default();
    if host.closing() {
        return Ok(());
    }
    say(host, "claude", Some(said));
    resend_waiting_login_requests(host, "claude").await;
    Ok(())
}

/// `ResetClaudeBedrock` (D9): asked; then Bedrock taken out of the user's Claude settings (with a backup) and the models reset. A
/// machine change: only with `--allow-state-write`, never in a smoke.
async fn reset_claude_bedrock(host: &Arc<Host>) -> Result<(), String> {
    require_idle_account(host, "claude")?;
    if crate::native::show_dialog(host, mighty_accounts::view::reset_bedrock_dialog()).await != DialogResult::Primary {
        return Ok(());
    }
    require_idle_account(host, "claude")?;
    if !host.may_change_machine() {
        say(host, "claude", Some(verification()));
        return Ok(());
    }
    let environment = match host.runs() {
        Some(runs) => runs.catalog.refresh_environment(true).await,
        None => std::env::vars().collect(),
    };
    if host.closing() {
        return Ok(());
    }
    require_idle_account(host, "claude")?;
    mighty_accounts::bedrock::reset_user_settings(&home(), &environment).map_err(|e| e.to_string())?;
    reset_claude_models(host).await
}

/// The CLI tab's buttons, each the C# button's action through `Then`: the row held while it works, the rows drawn again after.
pub struct TabActions {
    pub host: Arc<Host>,
}

impl TabActions {
    fn then(&self, provider: &str, work: impl std::future::Future<Output = Result<(), String>> + Send + 'static) {
        let (host, provider) = (self.host.clone(), provider.to_string());
        lock(&of(&host).working).insert(provider.clone());
        redraw(&host);
        crate::native::act(&self.host, async move {
            let outcome = work.await;
            lock(&of(&host).working).remove(&provider);
            redraw(&host);
            outcome
        });
    }
}

impl mighty_accounts::view::AccountActions for TabActions {
    fn change(&self, provider: &str) {
        let (host, p) = (self.host.clone(), provider.to_string());
        self.then(provider, async move { confirm_cli_sign_out(&host, &p, true).await });
    }
    fn logout(&self, provider: &str) {
        let (host, p) = (self.host.clone(), provider.to_string());
        self.then(provider, async move { confirm_cli_sign_out(&host, &p, false).await });
    }
    fn sign_in(&self, provider: &str, option: CliLoginOption) {
        let (host, p) = (self.host.clone(), provider.to_string());
        self.then(provider, async move { start_cli_sign_in(&host, &p, option).await });
    }
    fn cancel_wait(&self, provider: &str) {
        let wait = lock(&of(&self.host).waits).get(provider).cloned();
        if let Some(wait) = wait {
            wait.send_replace(true);
        }
    }
    fn reset_models(&self) {
        let host = self.host.clone();
        self.then("claude", async move { reset_claude_models(&host).await });
    }
    fn reset_bedrock(&self) {
        let host = self.host.clone();
        self.then("claude", async move { reset_claude_bedrock(&host).await });
    }
    fn refresh(&self, provider: &str) {
        let (host, p) = (self.host.clone(), provider.to_string());
        self.then(provider, async move {
            say(&host, &p, None);
            if let Some(coordinator) = coordinator(&host).filter(|_| host.smoke.is_none()) {
                coordinator.refresh(&[p.as_str()]).await;
            }
            Ok(())
        });
    }
    fn auto_login(&self, on: bool) {
        let host = self.host.clone();
        crate::native::act(&self.host, async move {
            host.view().update(|state| state.auto_login_clis = Some(on))?;
            host.schedule();
            Ok(())
        });
    }
}

/// The CLI accounts box's model (`BuildCliAccountsSectionFromState`): the smoke's fixture statuses while its step shows them, else
/// each provider's last status ("checking" until read), with the rows' waits, messages and work.
pub fn accounts_model(host: &Host, fixture: Option<Vec<CliAccountStatus>>) -> mighty_accounts::view::AccountsModel {
    let accounts = of(host);
    let read = accounts.coordinator.get().map(|c| c.statuses()).unwrap_or_default();
    model_of(&accounts, fixture, &read, || host.view().snapshot.auto_login_clis != Some(false))
}

/// The CLI accounts box's model from the accounts' state: `auto_login` (it reads the state) runs before any account lock is taken, and
/// each map is copied under its own lock, so a builder never holds an account lock while the state's is taken (`login_input` and
/// `busy` take account locks with the state locked).
fn model_of(
    accounts: &Accounts,
    fixture: Option<Vec<CliAccountStatus>>,
    read: &HashMap<String, CliAccountStatus>,
    auto_login: impl FnOnce() -> bool,
) -> mighty_accounts::view::AccountsModel {
    let auto_login = auto_login();
    let statuses = fixture.unwrap_or_else(|| mighty_accounts::view::AccountsModel::from_statuses(read));
    let waits: HashSet<String> = lock(&accounts.waits).keys().cloned().collect();
    let messages = lock(&accounts.messages).clone();
    let working = lock(&accounts.working).clone();
    let rows = statuses
        .into_iter()
        .map(|status| mighty_accounts::view::AccountRow {
            pending: waits.contains(&status.provider),
            message: messages.get(&status.provider).cloned(),
            working: working.contains(&status.provider),
            status,
        })
        .collect();
    mighty_accounts::view::AccountsModel { rows, reading: accounts.reading.load(Ordering::SeqCst) > 0, auto_login }
}

/// The providers box's model: the run engine's runtime per provider, and the recheck under way.
pub fn providers_model(host: &Host) -> mighty_accounts::view::ProvidersModel {
    // A smoke shows the runtime as not read yet, as C#'s does (it never reads it in a smoke): never this PC's CLIs.
    if host.smoke.is_some() {
        return mighty_accounts::view::ProvidersModel { runtime: vec![], checking: of(host).checking_runtime.load(Ordering::SeqCst) };
    }
    let runtime = host.runs().map(|runs| mighty_core::wire::PROVIDERS.iter().filter_map(|p| runs.runtime(p)).collect()).unwrap_or_default();
    mighty_accounts::view::ProvidersModel { runtime, checking: of(host).checking_runtime.load(Ordering::SeqCst) }
}

/// The providers box's recheck (`RefreshRuntime`): the CLIs found again and read, the button waiting meanwhile.
pub fn recheck_runtime(host: &Arc<Host>) {
    let accounts = of(host);
    if accounts.checking_runtime.swap(true, Ordering::SeqCst) {
        return;
    }
    redraw(host);
    let host = host.clone();
    tauri::async_runtime::spawn(async move {
        crate::models::reload_runtime(&host).await;
        of(&host).checking_runtime.store(false, Ordering::SeqCst);
        redraw(&host);
    });
}

/// The smoke's sign-in card (the `loginCard` step): a fixture retry of `pane` kept as if its run had lost the sign-in.
pub fn smoke_retry(host: &Host, pane: &RunSession) {
    let mut request = StartRunRequest::new(pane.id(), pane.workspace_id(), "claude", "smoke sign-in fixture");
    request.provider = pane.provider().to_string();
    let accounts = of(host);
    let mut book = lock(&accounts.book);
    book.sent(&request, None);
    if let Some(retry) = book.settled(pane.id()) {
        book.remember(retry);
    }
}

/// Whether the pane's sign-in card is up (its retry is kept).
pub fn has_retry(host: &Host, pane: &str) -> bool {
    lock(&of(host).book).requests().contains_key(pane)
}

/// Whether the account changes or a button of its row works (the smoke's D8 waits for it to settle).
pub fn is_changing(accounts: &Accounts, provider: &str) -> bool {
    let working = lock(&accounts.working).contains(provider);
    working || accounts.is_changing(provider)
}

/// Whether a sign-out was confirmed in D8, before any gate (the smoke's D8 cancelled must leave none).
pub fn signed_out_in_smoke(host: &Host) -> bool {
    of(host).sign_out_attempts.load(Ordering::SeqCst) > 0
}

/// `RefreshLoginCards`' upkeep (MainWindow.LoginRecovery.cs:96-103), on every frame pass with the state locked: a retry whose pane is
/// gone or now runs another provider is dropped, then every sign-in no pane waits on any more ends. Takes account locks only (never the
/// state's: `snapshot` is the locked state) and draws nothing: the next pass shows it.
pub fn prune(host: &Host, snapshot: &AppSnapshot) {
    prune_of(&of(host), snapshot);
}

fn prune_of(accounts: &Accounts, snapshot: &AppSnapshot) {
    let gone: Vec<String> = lock(&accounts.book)
        .requests()
        .iter()
        .filter(|(id, retry)| snapshot.session(id).is_none_or(|pane| pane.provider() != retry.request.provider))
        .map(|(id, _)| id.clone())
        .collect();
    for id in &gone {
        lock(&accounts.book).drop_session(id);
        lock(&accounts.notes).remove(id);
        lock(&accounts.awaiting_update).remove(id);
    }
    let providers: Vec<String> = lock(&accounts.jobs).keys().cloned().collect();
    for provider in providers {
        cancel_unused(accounts, &provider);
    }
}

/// `ShutdownLoginRecovery` (MainWindow.LoginRecovery.cs:313-319): every sign-in ended (its hidden terminal closed by its task), every
/// terminal wait cancelled, the deferred starts, retries, notes and held resends forgotten; no sign-in starts from here on. Called
/// once from the app's exit.
pub fn shutdown(host: &Host) {
    let accounts = of(host);
    accounts.closed.store(true, Ordering::SeqCst);
    let providers: Vec<String> = lock(&accounts.jobs).keys().cloned().collect();
    for provider in providers {
        cancel_background_login(host, &provider);
    }
    let waits: Vec<watch::Sender<bool>> = lock(&accounts.waits).drain().map(|(_, wait)| wait).collect();
    for wait in waits {
        wait.send_replace(true);
    }
    lock(&accounts.book).clear();
    lock(&accounts.notes).clear();
    lock(&accounts.awaiting_update).clear();
    lock(&accounts.deferred).clear();
}

#[cfg(test)]
mod tests {
    use mighty_core::Wire;
    use mighty_core::locale::Locale;

    use super::*;

    /// Review C1: the CLI tab's model reads the state (its auto-login switch) before it takes any account lock, so it never waits on
    /// the state's lock while holding one that a pass holding the state's lock waits on (`login_input`, `busy`). Before, it held the
    /// waits, messages and work maps across `host.view()`: these two threads deadlocked.
    #[test]
    fn the_tabs_model_never_holds_an_account_lock_while_the_state_is_taken() {
        let accounts = Arc::new(Accounts::default());
        lock(&accounts.waits).insert("pi".into(), watch::channel(false).0);
        lock(&accounts.messages).insert("pi".into(), "hint".into());
        let state = Arc::new(Mutex::new(()));
        let (done, finished) = std::sync::mpsc::channel();
        let (a, a_state, a_done) = (accounts.clone(), state.clone(), done.clone());
        std::thread::spawn(move || {
            for _ in 0..3000 {
                let _locked = lock(&a_state);
                let _ = a.is_waiting("pi") || a.is_busy("pi") || a.is_changing("pi");
                let _ = lock(&a.messages).len();
            }
            let _ = a_done.send(());
        });
        let (b, b_state) = (accounts.clone(), state.clone());
        std::thread::spawn(move || {
            for _ in 0..3000 {
                let model = model_of(&b, None, &HashMap::new(), || {
                    let _locked = lock(&b_state);
                    true
                });
                assert!(model.rows.iter().any(|r| r.status.provider == "pi" && r.pending && r.message.as_deref() == Some("hint")));
            }
            let _ = done.send(());
        });
        for _ in 0..2 {
            finished.recv_timeout(Duration::from_secs(30)).expect("the tab's model and a locked pass deadlocked");
        }
    }

    /// Review C2: a new start of a terminal sign-in waits on the window before only while that window is the console host's and still
    /// open; a Windows Terminal tab handed its command off, so the next start opens a new one. The wait itself counts a tab as open.
    #[test]
    fn only_an_open_console_window_is_waited_on_again() {
        let tab = LoginWindow { holds_command: false, closed: Arc::new(AtomicBool::new(true)) };
        assert!(!tab.reusable(), "a Windows Terminal tab is never reused, even after its launcher ended");
        assert!(tab.open(), "the wait cannot see a tab close");
        let console = LoginWindow { holds_command: true, closed: Arc::new(AtomicBool::new(false)) };
        assert!(console.reusable() && console.open(), "a console window still open is waited on again");
        console.closed.store(true, Ordering::SeqCst);
        assert!(!console.reusable() && !console.open(), "a closed console window opens a new one and ends the wait");
    }

    /// Review C4 (C# `RefreshLoginCards`): a retry whose pane is gone, or now runs another provider, is dropped, and a sign-in no pane
    /// waits on any more ends; one still waited on stays.
    #[test]
    fn retries_of_closed_panes_go_and_their_sign_ins_end() {
        mighty_run::strings::set_language("ko");
        let accounts = Accounts::default();
        let keep = |id: &str, provider: &str| {
            let mut request = StartRunRequest::new(id, "w", "claude", "again");
            request.provider = provider.into();
            let mut book = lock(&accounts.book);
            book.sent(&request, None);
            let retry = book.settled(id).unwrap();
            book.remember(retry);
        };
        keep("kept", "claude");
        keep("closed", "codex");
        keep("switched", "gemini");
        let job = |provider: &str| {
            let (cancel, cancelled) = watch::channel(false);
            lock(&accounts.jobs).insert(
                provider.into(),
                Job { serial: 1, automatic: true, terminal: false, phase: "waiting".into(), process: None, cancel },
            );
            cancelled
        };
        let (claude, codex) = (job("claude"), job("codex"));
        let snapshot = AppSnapshot::read(&serde_json::json!({
            "workspaces": [{"id": "w", "path": "/fixture"}],
            "sessions": [
                {"id": "kept", "workspaceId": "w", "kind": "claude", "provider": "claude"},
                {"id": "switched", "workspaceId": "w", "kind": "claude", "provider": "pi"},
            ],
        }))
        .unwrap();
        prune_of(&accounts, &snapshot);
        let left: Vec<String> = lock(&accounts.book).requests().keys().cloned().collect();
        assert_eq!(left, ["kept"], "only the live pane's retry stays");
        assert!(accounts.has_job("claude") && !*claude.borrow(), "a sign-in still waited on goes on");
        assert!(!accounts.has_job("codex") && *codex.borrow(), "the closed pane's sign-in ends");
    }

    /// The source-grep halves of C#'s L1400 and L1427 as the app's registry: the sign-in card's, the usage popover's and the CLI tab's
    /// methods are registered (`loginAction`, `usageRefresh`, `usageDirect`; the native forms' through `nativeAction`), and Settings'
    /// CLI category draws the providers, the accounts (every provider's row: tests/views.rs) and Claude Mods with the CLI updates.
    #[test]
    fn the_accounts_and_usage_are_wired_into_the_app() {
        let names = crate::methods::registry().names();
        for method in ["loginAction", "usageRefresh", "usageDirect", "nativeAction", "openSettings"] {
            assert!(names.contains(method), "{method} is registered");
        }
        let locale = Locale::new("ko");
        let boxes: Vec<crate::settings::Builder> = crate::settings::groups(&locale, "cli").into_iter().map(|g| g.build).collect();
        let ours: [crate::settings::Builder; 3] =
            [crate::settings::cli_accounts::providers, crate::settings::cli_accounts::accounts, crate::settings::cli_accounts::claude_mods];
        assert!(ours.iter().all(|b| boxes.iter().any(|g| std::ptr::fn_addr_eq(*g, *b))), "the CLI tab draws the accounts' boxes");
    }
}
