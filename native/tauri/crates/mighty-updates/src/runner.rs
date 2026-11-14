//! The one-shot CLI run every update and plugin call goes through (C# `ICliRunner`, `CliRunResult`), and the app's implementation
//! over the platform's `ProcessHost` (C# `CliRunner`): stdin closed at once, both outputs drained from the start and each kept to a
//! cap, the child killed with everything it started when its time runs out or the caller cancels.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use mighty_platform::process::ChildReader;
use mighty_platform::{BoxFuture, ProcessHost};
use tokio::io::AsyncReadExt;
use tokio::sync::watch;

/// C#'s `CancellationToken`: `CancellationToken::new()` is `CancellationToken.None` until cancelled, and a `child_token()` is
/// `CancellationTokenSource.CreateLinkedTokenSource(token)`.
pub use tokio_util::sync::CancellationToken;

/// C# `CliRunResult`: the exit code (-1 when the time limit killed the child), both outputs as text, and whether it timed out.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct CliRunResult {
    pub exit_code: i32,
    pub output: String,
    pub error_output: String,
    pub timed_out: bool,
}

impl CliRunResult {
    pub fn new(exit_code: i32, output: impl Into<String>, error_output: impl Into<String>, timed_out: bool) -> CliRunResult {
        CliRunResult { exit_code, output: output.into(), error_output: error_output.into(), timed_out }
    }
}

/// One run: never a shell command line, an executable's full path and its argument list (C# `RunAsync`'s parameters).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CliCall {
    pub executable: String,
    pub arguments: Vec<String>,
    pub timeout: Duration,
    /// Laid over the app's own environment, name by name (C# `ProcessStartInfo.Environment[key] = value`).
    pub environment: Option<BTreeMap<String, String>>,
    /// Names the child must not inherit from the app's environment (unless `environment` sets one again). C# has no such field: a
    /// name C# drops from the dictionary it hands over is still inherited from the app; this one is not (`FORCE_AUTOUPDATE_PLUGINS`).
    pub without: Vec<String>,
    /// An existing folder, else the app's working folder.
    pub working_directory: Option<String>,
}

impl CliCall {
    pub fn new(executable: impl Into<String>, arguments: impl IntoIterator<Item = impl Into<String>>, timeout: Duration) -> CliCall {
        CliCall {
            executable: executable.into(),
            arguments: arguments.into_iter().map(Into::into).collect(),
            timeout,
            environment: None,
            without: Vec::new(),
            working_directory: None,
        }
    }

    pub fn environment(mut self, environment: Option<BTreeMap<String, String>>) -> CliCall {
        self.environment = environment;
        self
    }

    pub fn working_directory(mut self, folder: Option<String>) -> CliCall {
        self.working_directory = folder;
        self
    }

    pub fn without(mut self, names: impl IntoIterator<Item = impl Into<String>>) -> CliCall {
        self.without.extend(names.into_iter().map(Into::into));
        self
    }
}

/// Why a run gave no result: the caller's token was cancelled (C#'s `OperationCanceledException`), or the child could not be
/// started or waited for (any other exception, with its message).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RunError {
    Cancelled,
    Failed(String),
}

impl std::fmt::Display for RunError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            RunError::Cancelled => f.write_str("The operation was canceled."),
            RunError::Failed(message) => f.write_str(message),
        }
    }
}

/// C# `ICliRunner`: runs one CLI to its end. Checks inject a fake, so no real claude, codex, npm or winget ever starts.
pub trait CliRunner: Send + Sync {
    fn run(&self, call: CliCall, cancel: CancellationToken) -> BoxFuture<'_, Result<CliRunResult, RunError>>;
}

/// C# `CliRunner` over the platform's `ProcessHost` (`ProcessHost::cli_request`: on Windows a `.cmd`/`.bat` shim runs through
/// `cmd.exe /d /s /c`). The executable is always a full path the caller found, so there is no PATH lookup here.
pub struct HostCliRunner {
    host: Arc<dyn ProcessHost>,
    cap: usize,
}

impl HostCliRunner {
    /// 1 MiB cap on each captured output — the macOS updater limit (CliRunner.cs:20-21).
    pub const OUTPUT_CAP_BYTES: usize = 1024 * 1024;

    pub fn new(host: Arc<dyn ProcessHost>) -> HostCliRunner {
        HostCliRunner { host, cap: Self::OUTPUT_CAP_BYTES }
    }

    /// The cap on each output; 0 means the default (CliRunner.cs:34).
    pub fn with_output_cap(mut self, bytes: usize) -> HostCliRunner {
        self.cap = if bytes > 0 { bytes } else { Self::OUTPUT_CAP_BYTES };
        self
    }

    pub fn output_cap(&self) -> usize {
        self.cap
    }

    async fn run_call(&self, call: CliCall, cancel: CancellationToken) -> Result<CliRunResult, RunError> {
        if cancel.is_cancelled() {
            return Err(RunError::Cancelled);
        }
        let cwd = match call.working_directory.as_deref() {
            Some(folder) if !folder.is_empty() && Path::new(folder).is_dir() => PathBuf::from(folder),
            _ => std::env::current_dir().map_err(|error| RunError::Failed(error.to_string()))?,
        };
        let environment = call.environment.unwrap_or_default();
        let mut request = self
            .host
            .cli_request(Path::new(&call.executable), &call.arguments, &cwd, &environment)
            .map_err(|error| RunError::Failed(error.to_string()))?;
        for name in call.without {
            request = request.without(name);
        }
        let mut child = self.host.spawn(request).await.map_err(|error| RunError::Failed(error.to_string()))?;
        let control = child.control();
        drop(child.stdin.take());
        let output = spawn_reader(child.stdout.take(), self.cap);
        let error = spawn_reader(child.stderr.take(), self.cap);
        let waited = tokio::select! {
            _ = cancel.cancelled() => None,
            waited = tokio::time::timeout(call.timeout, control.wait()) => Some(waited),
        };
        let (exit_code, timed_out) = match waited {
            None => {
                // The caller's token: the child and everything it started end, and the cancel propagates (C#'s DisposeAsync).
                control.kill();
                let _ = control.close().await;
                return Err(RunError::Cancelled);
            }
            Some(Ok(Ok(code))) => (code, false),
            Some(Ok(Err(failure))) => {
                let _ = control.close().await;
                return Err(RunError::Failed(failure.to_string()));
            }
            Some(Err(_)) => {
                // The time limit ran out; kill the child together with its whole group.
                control.kill();
                (-1, true)
            }
        };
        let grace = Duration::from_millis(if timed_out { 1000 } else { 500 });
        let (mut out, mut err) = (output.clone(), error.clone());
        let _ = tokio::time::timeout(grace, async {
            let _ = out.wait_for(Option::is_some).await;
            let _ = err.wait_for(Option::is_some).await;
        })
        .await;
        let result = CliRunResult {
            exit_code,
            output: output.borrow().clone().unwrap_or_default(),
            error_output: error.borrow().clone().unwrap_or_default(),
            timed_out,
        };
        control.close().await.map_err(|failure| RunError::Failed(failure.to_string()))?;
        Ok(result)
    }
}

impl CliRunner for HostCliRunner {
    fn run(&self, call: CliCall, cancel: CancellationToken) -> BoxFuture<'_, Result<CliRunResult, RunError>> {
        Box::pin(self.run_call(call, cancel))
    }
}

/// One output read on a task of its own; its text appears in the channel once the pipe has ended.
fn spawn_reader(reader: Option<ChildReader>, cap: usize) -> watch::Receiver<Option<String>> {
    let (sender, receiver) = watch::channel(None);
    tokio::spawn(async move {
        let text = read_capped(reader, cap).await;
        sender.send_replace(Some(text));
    });
    receiver
}

/// At most `cap` bytes of UTF-8 kept, the pipe drained to its end so a talkative child never blocks (C# `ReadCappedAsync`); a
/// character the cap cuts through is left out whole.
async fn read_capped(reader: Option<ChildReader>, cap: usize) -> String {
    let Some(mut reader) = reader else { return String::new() };
    let mut kept = Vec::new();
    let mut buffer = vec![0u8; 8192];
    loop {
        match reader.read(&mut buffer).await {
            Ok(0) | Err(_) => break,
            Ok(read) => {
                let room = cap.saturating_sub(kept.len());
                kept.extend_from_slice(&buffer[..read.min(room)]);
            }
        }
    }
    if let Err(error) = std::str::from_utf8(&kept)
        && error.error_len().is_none()
    {
        kept.truncate(error.valid_up_to());
    }
    String::from_utf8_lossy(&kept).into_owned()
}
