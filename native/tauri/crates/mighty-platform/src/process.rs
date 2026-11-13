//! Child processes with redirected standard streams (C# `ChildProcess`, Swift `NativeChildProcess` / `ProcessCapture`).

use std::collections::BTreeMap;
use std::fmt;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};

use crate::{BoxFuture, PlatformError, Result};

pub type ChildReader = Box<dyn AsyncRead + Send + Unpin>;
pub type ChildWriter = Box<dyn AsyncWrite + Send + Unpin>;

/// What to start. The program is a full path: finding a bare name on `PATH` (and `PATHEXT`) is the caller's, as the CLI locators do.
#[derive(Clone)]
pub struct SpawnRequest {
    pub program: PathBuf,
    pub args: Vec<String>,
    /// An existing absolute folder.
    pub cwd: PathBuf,
    /// Laid over this process's environment, name by name (Windows compares names ignoring case). An empty value stays set and
    /// empty: that is how a credential a fresh shell profile dropped is kept from being inherited (C# `WindowsShellEnvironment.Merge`).
    pub env: BTreeMap<String, String>,
    /// Windows only: run `program` (cmd.exe) as `cmd.exe /d /s /c "<command>"` — how a `.cmd`/`.bat` shim starts. Elsewhere the
    /// spawn answers `Invalid`.
    pub shell_command: Option<String>,
}

impl SpawnRequest {
    /// A request as C# `ChildProcess.StartInfo` builds one: `NO_COLOR=1` set, nothing else changed.
    pub fn new(program: impl Into<PathBuf>, args: impl IntoIterator<Item = impl Into<String>>, cwd: impl Into<PathBuf>) -> SpawnRequest {
        let mut env = BTreeMap::new();
        env.insert("NO_COLOR".to_string(), "1".to_string());
        SpawnRequest {
            program: program.into(),
            args: args.into_iter().map(Into::into).collect(),
            cwd: cwd.into(),
            env,
            shell_command: None,
        }
    }

    pub fn env(mut self, name: impl Into<String>, value: impl Into<String>) -> SpawnRequest {
        self.env.insert(name.into(), value.into());
        self
    }
}

/// The environment's names only: values can be credentials.
impl fmt::Debug for SpawnRequest {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("SpawnRequest")
            .field("program", &self.program)
            .field("args", &self.args.len())
            .field("cwd", &self.cwd)
            .field("env", &self.env.keys().collect::<Vec<_>>())
            .field("shell_command", &self.shell_command.is_some())
            .finish()
    }
}

/// A started child: its three pipes (each taken once) and its control. Dropping the pipes closes them; dropping the child does not
/// end it — call [`Child::close`].
pub struct Child {
    pub id: u32,
    pub stdin: Option<ChildWriter>,
    pub stdout: Option<ChildReader>,
    pub stderr: Option<ChildReader>,
    control: Arc<dyn ChildControl>,
}

impl Child {
    pub fn new(id: u32, stdin: ChildWriter, stdout: ChildReader, stderr: ChildReader, control: Arc<dyn ChildControl>) -> Child {
        Child { id, stdin: Some(stdin), stdout: Some(stdout), stderr: Some(stderr), control }
    }

    /// The control alone, for a task that kills or waits while another reads the pipes.
    pub fn control(&self) -> Arc<dyn ChildControl> {
        self.control.clone()
    }

    pub fn kill(&self) {
        self.control.kill();
    }

    pub async fn wait(&self) -> Result<i32> {
        self.control.wait().await
    }

    /// Closes the pipes, then [`ChildControl::close`].
    pub async fn close(self) -> Result<()> {
        let Child { control, .. } = self;
        control.close().await
    }
}

impl fmt::Debug for Child {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Child").field("id", &self.id).finish_non_exhaustive()
    }
}

/// Ending and waiting for one child.
pub trait ChildControl: Send + Sync {
    /// Ends the child **and everything it started**, at once and without asking (Windows: `TerminateJobObject`; the Mac: `SIGKILL`
    /// to its process group). Calling it again, or after the exit, does nothing.
    fn kill(&self);
    /// The exit code, once the child has exited (Windows: the DWORD as a signed number, as C# reads it).
    fn wait(&self) -> BoxFuture<'_, Result<i32>>;
    /// `kill`, then waits up to 3 s for the exit and, on Windows, up to 3 s more until the job holds no process, so no descendant
    /// still has the workspace's files open (C# `ChildProcess.DisposeAsync`). `TimedOut` when descendants remain.
    fn close(&self) -> BoxFuture<'_, Result<()>>;
}

/// Starts child processes.
///
/// Contract:
/// - stdin, stdout and stderr are always pipes; text is UTF-8 both ways (Windows: the app keeps one hidden console of its own,
///   code page 65001, that children inherit, so cmd built-ins and native CLIs write UTF-8 into the pipes).
/// - No console window ever shows (Windows: `CREATE_NO_WINDOW` semantics).
/// - The child can never outlive the app: on Windows it is created suspended, joins a job object with
///   `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` and only then runs its first instruction, so everything it starts is in the job too and
///   dies when the app's job handle closes — at [`ChildControl::close`], or when the app exits or crashes. On the Mac the child leads
///   a new process group that `kill` signals whole.
/// - Only the given pipes are inherited (no other handle of the app leaks into the child).
pub trait ProcessHost: Send + Sync {
    fn spawn(&self, request: SpawnRequest) -> BoxFuture<'_, Result<Child>>;
}

/// What [`capture`] collected.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Captured {
    pub exit_code: i32,
    pub stdout: Vec<u8>,
    pub stderr: Vec<u8>,
}

/// Runs a child to its end (Swift `ProcessCapture.run`): writes `input` and closes stdin, reads both outputs (each at most
/// `max_bytes`), waits. Past `timeout`, or with too much output, the child is killed and closed and the capture fails.
pub async fn capture(
    host: &dyn ProcessHost,
    request: SpawnRequest,
    input: Option<Vec<u8>>,
    timeout: Duration,
    max_bytes: usize,
) -> Result<Captured> {
    let mut child = host.spawn(request).await?;
    let control = child.control();
    let (stdin, stdout, stderr) = (child.stdin.take(), child.stdout.take(), child.stderr.take());
    let waiting = control.clone();
    let work = async move {
        let write = async move {
            if let Some(mut pipe) = stdin {
                if let Some(input) = input {
                    pipe.write_all(&input).await?;
                }
                pipe.shutdown().await?;
            }
            Ok::<(), PlatformError>(())
        };
        let (written, out, err) = tokio::join!(write, read_bounded(stdout, max_bytes), read_bounded(stderr, max_bytes));
        // A child that closed its stdin before reading all of it is not a failure of the capture.
        drop(written);
        let exit_code = waiting.wait().await?;
        Ok::<Captured, PlatformError>(Captured { exit_code, stdout: out?, stderr: err? })
    };
    let outcome = match tokio::time::timeout(timeout, work).await {
        Ok(outcome) => outcome,
        Err(_) => Err(PlatformError::TimedOut("capture")),
    };
    if outcome.is_err() {
        control.kill();
    }
    let closed = control.close().await;
    let captured = outcome?;
    closed?;
    Ok(captured)
}

async fn read_bounded(reader: Option<ChildReader>, max_bytes: usize) -> Result<Vec<u8>> {
    let Some(mut reader) = reader else { return Ok(vec![]) };
    let mut out = Vec::new();
    let mut buffer = [0u8; 8192];
    loop {
        let read = reader.read(&mut buffer).await?;
        if read == 0 {
            return Ok(out);
        }
        if out.len() + read > max_bytes {
            return Err(PlatformError::TooLarge(max_bytes));
        }
        out.extend_from_slice(&buffer[..read]);
    }
}
