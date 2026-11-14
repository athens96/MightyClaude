//! Running the status line command (C# `StatusLineResult`, `StatusLineSupport.Shell`, `RunAsync`, `ReadCappedAsync`, `ParseLines`,
//! StatusLine.cs:208-521) through the platform's `ProcessHost`: the shell Claude Code itself uses, the payload on stdin, at most
//! 16 KiB of stdout kept (whole characters), stderr drained and never kept, a deadline, and a cancellation (the pane's refresher
//! closing) that ends the command and everything it started before it returns.

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use mighty_platform::process::ChildReader;
use mighty_platform::{ProcessHost, SpawnRequest};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::sync::watch;

use crate::status_line::ansi::{self, AnsiSegment};
use crate::status_line::config::StatusLineConfig;
use crate::status_line::payload::{self, StatusLineContext};
use crate::status_line::strings;

/// At most this many rows are shown.
pub const MAXIMUM_LINES: usize = 6;
const MAXIMUM_OUTPUT_BYTES: usize = 16 * 1024;
/// C#'s default deadline, in seconds.
pub const DEFAULT_TIMEOUT: Duration = Duration::from_secs(8);

/// C# `StatusLineResult(Lines, ErrorText?, ExitCode, TimedOut)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StatusLineResult {
    pub lines: Vec<Vec<AnsiSegment>>,
    pub error_text: Option<String>,
    pub exit_code: i32,
    pub timed_out: bool,
}

impl StatusLineResult {
    pub fn empty() -> StatusLineResult {
        StatusLineResult { lines: vec![], error_text: None, exit_code: 0, timed_out: false }
    }
}

/// The run was cancelled (its pane's refresher closed): nothing to show.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Cancelled;

/// A cancellation the refresher hands its runner (C#'s `CancellationToken`).
#[derive(Clone)]
pub struct Cancel(watch::Receiver<bool>);

impl Cancel {
    pub fn new(receiver: watch::Receiver<bool>) -> Cancel {
        Cancel(receiver)
    }

    /// One that never fires.
    pub fn never() -> Cancel {
        static NEVER: std::sync::OnceLock<watch::Sender<bool>> = std::sync::OnceLock::new();
        Cancel(NEVER.get_or_init(|| watch::channel(false).0).subscribe())
    }

    pub fn is_cancelled(&self) -> bool {
        *self.0.borrow()
    }

    /// Waits until it fires (for ever when the sender is gone without firing).
    pub async fn cancelled(&mut self) {
        if self.0.wait_for(|fired| *fired).await.is_err() {
            std::future::pending::<()>().await;
        }
    }
}

/// The shell Claude Code itself uses for a statusLine command, so a command that works in the CLI works here: `/bin/sh` elsewhere;
/// on Windows Git Bash when it is installed, PowerShell when it is not. `CLAUDE_CODE_GIT_BASH_PATH` names a Git Bash outside the
/// usual folders, as it does for the CLI, but only when its file name is bash or sh (a window launcher such as git-bash.exe would
/// open a terminal of its own at every refresh).
pub fn shell(
    command: &str,
    windows: bool,
    exists: &dyn Fn(&str) -> bool,
    environment: &dyn Fn(&str) -> Option<String>,
) -> (String, Vec<String>) {
    if !windows {
        return ("/bin/sh".into(), vec!["-c".into(), command.into()]);
    }
    let mut candidates = Vec::new();
    if let Some(named) = environment("CLAUDE_CODE_GIT_BASH_PATH").filter(|named| !named.is_empty() && names_bash(named)) {
        candidates.push(named);
    }
    let local = environment("LocalAppData").filter(|local| !local.is_empty()).map(|local| format!("{local}\\Programs"));
    for root in [environment("ProgramFiles"), environment("ProgramFiles(x86)"), local].into_iter().flatten().filter(|root| !root.is_empty())
    {
        candidates.push(format!("{root}\\Git\\bin\\bash.exe"));
    }
    if let Some(bash) = candidates.into_iter().find(|bash| exists(bash)) {
        return (bash, vec!["-c".into(), command.into()]);
    }
    let system = environment("SystemRoot").filter(|root| !root.is_empty()).unwrap_or_else(|| "C:\\Windows".into());
    (
        format!("{system}\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"),
        vec!["-NoProfile".into(), "-NonInteractive".into(), "-Command".into(), command.into()],
    )
}

fn names_bash(path: &str) -> bool {
    let name = path.rsplit(['\\', '/']).next().unwrap_or(path).to_lowercase();
    matches!(name.as_str(), "bash.exe" | "sh.exe" | "bash" | "sh")
}

/// [`shell`] over this process's environment and disk (C#'s defaults `File.Exists`, `Environment.GetEnvironmentVariable`).
pub fn system_shell(command: &str, windows: bool) -> (String, Vec<String>) {
    shell(command, windows, &|path| Path::new(path).is_file(), &|name| std::env::var(name).ok())
}

/// C# `RunAsync`: the configuration's command in the shell, the payload on stdin, at most `timeout`. The command runs where the CLI
/// would run it: the pane's folder when it exists, else this process's.
pub async fn run(
    processes: &dyn ProcessHost,
    config: &StatusLineConfig,
    ctx: &StatusLineContext,
    windows: bool,
    timeout: Duration,
    cancel: Cancel,
) -> Result<StatusLineResult, Cancelled> {
    let (binary, args) = system_shell(&config.command, windows);
    let cwd = ctx.cwd.as_deref().filter(|folder| !folder.is_empty() && Path::new(folder).is_dir()).map(PathBuf::from);
    let cwd = cwd.or_else(|| std::env::current_dir().ok()).unwrap_or_default();
    run_command(processes, &binary, &args, &cwd, ctx, timeout, cancel).await
}

/// [`run`] with the program given (the checks run the stand-in CLI here).
pub async fn run_command(
    processes: &dyn ProcessHost,
    binary: &str,
    args: &[String],
    cwd: &Path,
    ctx: &StatusLineContext,
    timeout: Duration,
    mut cancel: Cancel,
) -> Result<StatusLineResult, Cancelled> {
    if cancel.is_cancelled() {
        return Err(Cancelled);
    }
    let payload = payload::build_payload(ctx, chrono::Utc::now());
    let request = SpawnRequest::new(binary, args.iter().cloned(), cwd).env("CLAUDE_CODE_STATUSLINE_HOST", "mightyclaude");
    let mut child = match processes.spawn(request).await {
        Ok(child) => child,
        Err(error) => return Ok(start_failed(&error.to_string())),
    };
    let control = child.control();
    let output = Arc::new(Mutex::new(Vec::new()));
    let (out_done, mut out_finished) = watch::channel(false);
    let (err_done, err_finished) = watch::channel(false);
    spawn_reader(child.stdout.take(), Some(output.clone()), out_done);
    // Drained, never kept: a command's stderr may hold private text.
    spawn_reader(child.stderr.take(), None, err_done);
    let mut stdin = child.stdin.take();
    let work = async {
        if let Some(pipe) = stdin.as_mut() {
            // A command that does not read its input and ends first is no failure of the run.
            let _ = pipe.write_all(payload.as_bytes()).await;
            let _ = pipe.flush().await;
        }
        drop(stdin.take());
        control.wait().await
    };
    let outcome = tokio::select! {
        code = tokio::time::timeout(timeout, work) => Some(code),
        _ = cancel.cancelled() => None,
    };
    let result = match outcome {
        None => {
            // Closed: the command and everything it started end before this returns.
            control.kill();
            let _ = control.close().await;
            let mut err_finished = err_finished;
            let _ = tokio::time::timeout(Duration::from_millis(200), async {
                let _ = out_finished.wait_for(|done| *done).await;
                let _ = err_finished.wait_for(|done| *done).await;
            })
            .await;
            return Err(Cancelled);
        }
        Some(Ok(Ok(code))) => {
            // A short drain window for background children that hold stdout.
            let drained = tokio::time::timeout(Duration::from_millis(500), out_finished.wait_for(|done| *done)).await.is_ok();
            let text = if drained { decode_capped(&output) } else { String::new() };
            let error_text = (code != 0).then(|| strings::error_exit_template().replace("{status}", &code.to_string()));
            StatusLineResult { lines: parse_lines(&text), error_text, exit_code: code, timed_out: false }
        }
        Some(Ok(Err(error))) => start_failed(&error.to_string()),
        Some(Err(_)) => {
            control.kill();
            let _ = tokio::time::timeout(Duration::from_millis(200), out_finished.wait_for(|done| *done)).await;
            StatusLineResult { lines: parse_lines(""), error_text: Some(strings::error_timeout()), exit_code: -1, timed_out: true }
        }
    };
    let _ = control.close().await;
    Ok(result)
}

fn start_failed(error: &str) -> StatusLineResult {
    StatusLineResult {
        lines: vec![],
        error_text: Some(strings::error_start_template().replace("{error}", error)),
        exit_code: -1,
        timed_out: false,
    }
}

fn spawn_reader(pipe: Option<ChildReader>, keep: Option<Arc<Mutex<Vec<u8>>>>, done: watch::Sender<bool>) {
    tokio::spawn(async move {
        if let Some(mut pipe) = pipe {
            let mut buffer = [0u8; 4096];
            loop {
                match pipe.read(&mut buffer).await {
                    Ok(0) | Err(_) => break,
                    Ok(read) => {
                        if let Some(keep) = &keep {
                            // A few bytes past the cap, so the last whole character can still be told apart.
                            let mut kept = keep.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
                            let room = (MAXIMUM_OUTPUT_BYTES + 4).saturating_sub(kept.len()).min(read);
                            kept.extend_from_slice(&buffer[..room]);
                        }
                    }
                }
            }
        }
        let _ = done.send(true);
    });
}

/// C# `ReadCappedAsync`: the text decoded, then whole characters while they fit in 16 KiB of UTF-8.
fn decode_capped(bytes: &Arc<Mutex<Vec<u8>>>) -> String {
    let bytes = bytes.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    let text = String::from_utf8_lossy(&bytes);
    let mut kept = String::new();
    for c in text.chars() {
        if kept.len() + c.len_utf8() > MAXIMUM_OUTPUT_BYTES {
            break;
        }
        kept.push(c);
    }
    kept
}

/// C# `ParseLines`: the output without its trailing line ends, split at LF, at most six lines, each without its CR and parsed.
pub fn parse_lines(output: &str) -> Vec<Vec<AnsiSegment>> {
    output.trim_end_matches(['\n', '\r']).split('\n').take(MAXIMUM_LINES).map(|line| ansi::parse(line.trim_end_matches('\r'))).collect()
}
