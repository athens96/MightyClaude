//! Sign-in in a terminal window and the wait for it (C# `CliAccountTerminal`, CliAccounts.cs:568-620; `CliTerminalSignIn`,
//! `CliTerminalLogin`, `CliLoginWait`, CliLoginRecovery.cs:250-361).
//!
//! The window is opened through [`TerminalLauncher`], a port this crate defines until W6-A's `mighty_platform::console` lands: it
//! opens a command in a new console window that outlives the app (Windows: `CreateProcessW` with `CREATE_NEW_CONSOLE`, outside the
//! kill-on-close job, as C#'s `Process.Start`). [`RecordingTerminal`] is its fake: it records each launch and never opens a window.
//! Cancelling a wait is dropping its future.

use std::collections::BTreeMap;
use std::future::Future;
use std::path::PathBuf;
use std::sync::Mutex;
use std::time::Duration;

use mighty_platform::{BoxFuture, PlatformError};
use mighty_run::strings;
use tokio::sync::watch;
use tokio::time::Instant;

use crate::accounts::{self, CliAccountStatus};
use crate::{lock, login};

/// `WindowsCommand.NoCurrentDirectorySearch`: set, cmd.exe and the program look in no working folder for a bare name.
pub const NO_CURRENT_DIRECTORY_SEARCH: &str = "NoDefaultCurrentDirectoryInExePath";

/// Where the console host and cmd.exe are: the system folder Windows reports (`GetSystemDirectoryW`, which no variable of the
/// user's can move), never a name found along the PATH; by name only where no folder is known (the checks), as C#'s
/// `CliAccountTerminal.ConsoleHost` and `WindowsCommand.CommandInterpreter` fall back.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TerminalHosts {
    pub console_host: String,
    pub command_interpreter: String,
}

impl TerminalHosts {
    pub fn new(system_folder: Option<&str>) -> TerminalHosts {
        match system_folder.filter(|folder| !folder.is_empty()) {
            Some(folder) => TerminalHosts {
                console_host: mighty_run::os::combine(folder, "conhost.exe"),
                command_interpreter: mighty_run::os::combine(folder, "cmd.exe"),
            },
            None => TerminalHosts { console_host: "conhost.exe".into(), command_interpreter: "cmd.exe".into() },
        }
    }
}

/// `WindowsCommand.IsBatch`: a batch file, which only cmd.exe can run.
pub fn is_batch(path: &str) -> bool {
    let lower = path.to_ascii_lowercase();
    lower.ends_with(".cmd") || lower.ends_with(".bat")
}

/// `CliAccountTerminal.LaunchPlan`: the executable to start and its argument list, so that `login_argv` runs inside a new external
/// console window. Windows Terminal (`windows_terminal`, its full path as found along the PATH) is preferred: a new tab, titled
/// `title`, starting in `directory` (`-d`), then `--` and the command. A command, a path or a title holding `;` (Windows Terminal's
/// command separator) or `%` (which it expands) would be changed there, so such a command runs in the console host, and such a
/// folder or title is left out. The console host inherits the working folder and finds only an `.exe` by name, so the resolved full
/// path replaces the name there; a resolved `.cmd`/`.bat` shim runs through `cmd /c` in either window. An empty command is refused.
pub fn launch_plan(
    hosts: &TerminalHosts,
    login_argv: &[String],
    windows_terminal_available: bool,
    directory: Option<&str>,
    resolved_binary: Option<&str>,
    windows_terminal: Option<&str>,
    title: Option<&str>,
) -> mighty_platform::Result<(String, Vec<String>)> {
    if login_argv.first().is_none_or(|first| first.is_empty()) {
        return Err(PlatformError::Invalid("a login command is required".into()));
    }
    let splits = |text: Option<&str>| text.is_some_and(|t| t.contains(';') || t.contains('%'));
    let shim = resolved_binary.filter(|path| is_batch(path));
    let through_cmd = |binary: &str| {
        let mut args = vec![hosts.command_interpreter.clone(), "/c".into(), binary.to_string()];
        args.extend(login_argv[1..].iter().cloned());
        args
    };
    if windows_terminal_available
        && !splits(resolved_binary)
        && !splits(windows_terminal)
        && !login_argv.iter().any(|argument| splits(Some(argument)))
    {
        let mut tab = vec!["new-tab".to_string()];
        if let Some(title) = title.filter(|t| !t.is_empty() && !splits(Some(t))) {
            tab.extend(["--title".to_string(), title.to_string()]);
        }
        if let Some(directory) = directory.filter(|d| !splits(Some(d))) {
            tab.extend(["-d".to_string(), directory.to_string()]);
        }
        tab.push("--".into());
        match shim {
            Some(shim) => tab.extend(through_cmd(shim)),
            None => tab.extend(login_argv.iter().cloned()),
        }
        return Ok((windows_terminal.unwrap_or("wt.exe").to_string(), tab));
    }
    let args = match (resolved_binary, shim) {
        (None, _) => login_argv.to_vec(),
        (Some(_), Some(shim)) => through_cmd(shim),
        (Some(resolved), None) => std::iter::once(resolved.to_string()).chain(login_argv[1..].iter().cloned()).collect(),
    };
    Ok((hosts.console_host.clone(), args))
}

// ── the port ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// What a terminal window starts: `program` (a full path) with `args`, in `cwd` (else the app's folder), with `env` laid over this
/// process's environment.
#[derive(Clone, PartialEq, Eq)]
pub struct TerminalLaunch {
    pub program: String,
    pub args: Vec<String>,
    pub cwd: Option<PathBuf>,
    pub env: BTreeMap<String, String>,
}

/// The environment's names only: values can be credentials.
impl std::fmt::Debug for TerminalLaunch {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("TerminalLaunch")
            .field("program", &self.program)
            .field("args", &self.args)
            .field("cwd", &self.cwd)
            .field("env", &self.env.keys().collect::<Vec<_>>())
            .finish()
    }
}

/// An opened terminal window: its process's end (the console host's window closes with it; a Windows Terminal tab hands off and
/// ends at once).
pub trait TerminalWindow: Send {
    fn wait(&mut self) -> BoxFuture<'_, mighty_platform::Result<i32>>;
}

/// Opens a command in a terminal window of its own that outlives the app (C# `Process.Start` with a new console). It breaks the
/// `ProcessHost` rule that children die with the app on purpose, so it is a port of its own.
pub trait TerminalLauncher: Send + Sync {
    fn open(&self, launch: TerminalLaunch) -> BoxFuture<'_, mighty_platform::Result<Box<dyn TerminalWindow>>>;
}

/// The fake [`TerminalLauncher`]: records every launch, never opens a window; each window ends when the check says
/// ([`RecordingTerminal::close`]), and the next open can be made to fail.
#[derive(Default)]
pub struct RecordingTerminal {
    launches: Mutex<Vec<TerminalLaunch>>,
    exits: Mutex<Vec<watch::Sender<Option<i32>>>>,
    fail_next: Mutex<bool>,
}

impl RecordingTerminal {
    pub fn launches(&self) -> Vec<TerminalLaunch> {
        lock(&self.launches).clone()
    }

    /// The `index`th window's process ends with `code`.
    pub fn close(&self, index: usize, code: i32) {
        if let Some(exit) = lock(&self.exits).get(index) {
            exit.send_replace(Some(code));
        }
    }

    /// The next open fails, as a program that cannot start.
    pub fn fail_next(&self) {
        *lock(&self.fail_next) = true;
    }
}

struct RecordedWindow(watch::Receiver<Option<i32>>);

impl TerminalWindow for RecordedWindow {
    fn wait(&mut self) -> BoxFuture<'_, mighty_platform::Result<i32>> {
        Box::pin(async move {
            match self.0.wait_for(Option::is_some).await {
                Ok(code) => Ok(code.unwrap_or_default()),
                Err(_) => Err(PlatformError::Invalid("the fake terminal is gone".into())),
            }
        })
    }
}

impl TerminalLauncher for RecordingTerminal {
    fn open(&self, launch: TerminalLaunch) -> BoxFuture<'_, mighty_platform::Result<Box<dyn TerminalWindow>>> {
        Box::pin(async move {
            if std::mem::take(&mut *lock(&self.fail_next)) {
                return Err(PlatformError::Io(std::io::Error::other("the fake terminal refused to start")));
            }
            lock(&self.launches).push(launch);
            let (sender, receiver) = watch::channel(None);
            lock(&self.exits).push(sender);
            Ok(Box::new(RecordedWindow(receiver)) as Box<dyn TerminalWindow>)
        })
    }
}

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
