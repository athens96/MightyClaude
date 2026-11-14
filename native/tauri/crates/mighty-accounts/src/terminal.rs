//! Sign-in in a terminal window and the wait for it (C# `CliTerminalSignIn`, `CliTerminalLogin`, `CliLoginWait`,
//! CliLoginRecovery.cs:250-361).
//!
//! The window itself is mighty-platform's `TerminalLauncher` port (C# `CliAccountTerminal.LaunchPlan` with `Process.Start`: Windows
//! Terminal or the console host, in mighty-platform-windows' console.rs); the coordinator hands it the CLI's own sign-in
//! (`CliAccountsCoordinator::start_sign_in`). Cancelling a wait is dropping its future.

use std::future::Future;
use std::time::Duration;

use mighty_run::strings;
use tokio::sync::watch;
use tokio::time::Instant;

use crate::accounts::{self, CliAccountStatus};
use crate::login;

// ── the terminal sign-ins ─────────────────────────────────────────────────────────────────────────────────────────────────

/// `CliLoginOutcome`: how a wait on a sign-in ended.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CliLoginOutcome {
    LoggedIn,
    Exited,
    TimedOut,
}

/// `CliLoginWaitResult`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CliLoginWaitResult {
    pub outcome: CliLoginOutcome,
    pub status: CliAccountStatus,
}

/// C# `CliTerminalSignIn`: a provider whose sign-in is its interactive CLI, so it runs in a terminal the user sees. The command is
/// [`accounts::login_arguments`]; the sign-in is seen by asking the provider's status every interval ([`CliTerminalSignIn::wait`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CliTerminalSignIn {
    pub provider: &'static str,
    /// What the sign-in card says while the terminal waits.
    pub waiting_key: &'static str,
    /// The terminal's title, when it carries an instruction.
    pub title_key: Option<&'static str>,
}

/// `CliTerminalSignIn.Table`: Antigravity opens the browser on its first run; pi signs in inside itself (`/login`); opencode asks in
/// `opencode auth login`; Hermes in `hermes model`.
const TABLE: [CliTerminalSignIn; 4] = [
    CliTerminalSignIn { provider: "gemini", waiting_key: "loginRecovery.antigravityTerminal", title_key: None },
    CliTerminalSignIn { provider: "pi", waiting_key: "loginRecovery.piTerminal", title_key: Some("loginRecovery.piTerminalTitle") },
    CliTerminalSignIn {
        provider: "opencode",
        waiting_key: "loginRecovery.opencodeTerminal",
        title_key: Some("loginRecovery.opencodeTerminalTitle"),
    },
    CliTerminalSignIn {
        provider: "hermes",
        waiting_key: "loginRecovery.hermesTerminal",
        title_key: Some("loginRecovery.hermesTerminalTitle"),
    },
];

impl CliTerminalSignIn {
    /// `Table`, in C#'s order.
    pub fn table() -> &'static [CliTerminalSignIn] {
        &TABLE
    }

    /// `Table.TryGetValue(provider)`.
    pub fn get(provider: &str) -> Option<&'static CliTerminalSignIn> {
        TABLE.iter().find(|entry| entry.provider == provider)
    }

    /// `Command`: the command the terminal runs.
    pub fn command(&self) -> Option<Vec<&'static str>> {
        accounts::login_arguments(self.provider, accounts::CliLoginOption::Account)
    }

    /// `Title`: the table's instruction, else "<provider> 로그인".
    pub fn title(&self) -> String {
        strings::get_with(self.title_key.unwrap_or("loginRecovery.terminalTitle"), &[("provider", strings::provider_label(self.provider))])
    }

    /// `WaitingText`: what the sign-in card says while its sign-in waits: the provider's terminal sentence, or the background
    /// sign-in's.
    pub fn waiting_text(provider: &str) -> String {
        strings::get(CliTerminalSignIn::get(provider).map_or("loginRecovery.waiting", |entry| entry.waiting_key))
    }

    /// `WaitAsync`: signed in once `provider`'s status reads so (another provider's never counts); once more when the terminal
    /// closed (`Exited`) or the limit passed (`TimedOut`). Whether the terminal is open is asked every tick (1 s); the status every
    /// `interval` (3 s), for up to `limit` (10 minutes).
    pub async fn wait<S, F>(
        provider: &str,
        mut status: S,
        open: impl Fn() -> bool,
        interval: Option<Duration>,
        limit: Option<Duration>,
        tick: Option<Duration>,
    ) -> CliLoginWaitResult
    where
        S: FnMut() -> F,
        F: Future<Output = CliAccountStatus>,
    {
        let watch = Instant::now();
        let mut last_read = Duration::ZERO;
        let polling = interval.unwrap_or(Duration::from_secs(3));
        let timeout = limit.unwrap_or(Duration::from_secs(600));
        loop {
            tokio::time::sleep(tick.unwrap_or(Duration::from_secs(1))).await;
            let active = open();
            let elapsed = watch.elapsed();
            let expired = elapsed >= timeout;
            if active && !expired && elapsed - last_read < polling {
                continue;
            }
            last_read = elapsed;
            let value = status().await;
            if value.provider == provider && value.logged_in == Some(true) {
                return CliLoginWaitResult { outcome: CliLoginOutcome::LoggedIn, status: value };
            }
            if !active {
                return CliLoginWaitResult { outcome: CliLoginOutcome::Exited, status: value };
            }
            if expired {
                return CliLoginWaitResult { outcome: CliLoginOutcome::TimedOut, status: value };
            }
        }
    }
}

/// `CliLoginWait.RunAsync`: the wait on a hidden (background) sign-in whose process the app sees. An account already signed in
/// confirms only when the sign-in ended with exit 0; one signed out confirms once it turns signed in (read every `interval`, 3 s).
/// A stale signed-in status never confirms while the sign-in still runs. The process is looked at every `tick` (250 ms).
pub async fn login_wait<S, F>(
    running: impl Fn() -> bool,
    exit_code: impl Fn() -> Option<i32>,
    mut status: S,
    interval: Option<Duration>,
    limit: Option<Duration>,
    tick: Option<Duration>,
) -> CliLoginWaitResult
where
    S: FnMut() -> F,
    F: Future<Output = CliAccountStatus>,
{
    let watch = Instant::now();
    let was_signed_out = status().await.logged_in == Some(false);
    let mut last_check = watch.elapsed();
    let polling = interval.unwrap_or(Duration::from_secs(3));
    let timeout = limit.unwrap_or(Duration::from_secs(600));
    loop {
        tokio::time::sleep(tick.unwrap_or(Duration::from_millis(250))).await;
        let active = running();
        let elapsed = watch.elapsed();
        let expired = elapsed >= timeout;
        if !was_signed_out && active && !expired {
            continue;
        }
        if was_signed_out && active && !expired && elapsed - last_check < polling {
            continue;
        }
        last_check = elapsed;
        let value = status().await;
        let signed_in = value.logged_in == Some(true) && login::sign_in_can_fix(&value);
        if signed_in && (was_signed_out || !active && exit_code() == Some(0)) {
            return CliLoginWaitResult { outcome: CliLoginOutcome::LoggedIn, status: value };
        }
        if !active {
            return CliLoginWaitResult { outcome: CliLoginOutcome::Exited, status: value };
        }
        if expired {
            return CliLoginWaitResult { outcome: CliLoginOutcome::TimedOut, status: value };
        }
    }
}

/// `CliTerminalLoginResult`: how the wait on a terminal sign-in ended. `started_signed_out`: the account read as signed out when
/// the wait began.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CliTerminalLoginResult {
    pub outcome: CliLoginOutcome,
    pub status: CliAccountStatus,
    pub started_signed_out: bool,
}

/// C# `CliTerminalLogin`: the wait after a sign-in was started in a terminal the app cannot read. The status is read every 5 s for
/// up to 10 minutes and the wait ends once it reads signed in. Only a status that turned from signed out to signed in may send the
/// waiting requests again: signed out is the status held before the terminal opened (`before`), or the first reading when that one
/// says so. A terminal known to have closed (`closed` turns true) ends the wait with one last reading.
pub mod terminal_login {
    use super::*;

    /// `PollInterval`.
    pub const POLL_INTERVAL: Duration = Duration::from_secs(5);
    /// `PollLimit`.
    pub const POLL_LIMIT: Duration = Duration::from_secs(600);

    /// `SignedIn`.
    pub fn signed_in(status: &CliAccountStatus) -> bool {
        status.logged_in == Some(true) && status.access_verified != Some(false)
    }

    /// `Resends`: whether the requests that lost the sign-in are sent again by themselves.
    pub fn resends(result: &CliTerminalLoginResult) -> bool {
        result.started_signed_out && result.outcome == CliLoginOutcome::LoggedIn
    }

    /// `RunAsync`.
    pub async fn run<S, F>(
        mut status: S,
        closed: Option<watch::Receiver<bool>>,
        interval: Option<Duration>,
        limit: Option<Duration>,
        before: Option<&CliAccountStatus>,
    ) -> CliTerminalLoginResult
    where
        S: FnMut() -> F,
        F: Future<Output = CliAccountStatus>,
    {
        let watch = Instant::now();
        let polling = interval.unwrap_or(POLL_INTERVAL);
        let timeout = limit.unwrap_or(POLL_LIMIT);
        let before_signed_out = before.is_some_and(|b| b.logged_in == Some(false));
        let first = status().await;
        let started_signed_out = before_signed_out || first.logged_in == Some(false);
        if signed_in(&first) {
            return CliTerminalLoginResult { outcome: CliLoginOutcome::LoggedIn, status: first, started_signed_out: before_signed_out };
        }
        let mut closed = closed;
        loop {
            match closed.as_mut() {
                None => tokio::time::sleep(polling).await,
                Some(window) => {
                    // A window whose sender is gone without closing never ends the pause early.
                    let shut = async {
                        if window.wait_for(|shut| *shut).await.is_err() {
                            std::future::pending::<()>().await;
                        }
                    };
                    tokio::select! {
                        _ = tokio::time::sleep(polling) => {}
                        _ = shut => {}
                    }
                }
            }
            let value = status().await;
            if signed_in(&value) {
                return CliTerminalLoginResult { outcome: CliLoginOutcome::LoggedIn, status: value, started_signed_out };
            }
            if closed.as_ref().is_some_and(|window| *window.borrow()) {
                return CliTerminalLoginResult { outcome: CliLoginOutcome::Exited, status: value, started_signed_out };
            }
            if watch.elapsed() >= timeout {
                return CliTerminalLoginResult { outcome: CliLoginOutcome::TimedOut, status: value, started_signed_out };
            }
        }
    }
}
