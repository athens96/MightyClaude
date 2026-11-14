//! One command run to its end through the platform's `ProcessHost`, as C# `CliRunner.RunAsync` runs one (CliRunner.cs:39-104): the
//! input written and stdin closed, both outputs drained from the start and each kept to a cap while the pipe goes on being read, a
//! child past its time killed with everything it started and reported timed out with exit code -1 and what it had written. The
//! setup probe's `--version` reads and the toolkit's steps run through it.

use std::sync::{Arc, Mutex};
use std::time::Duration;

use mighty_platform::process::ChildReader;
use mighty_platform::{ProcessHost, Result, SpawnRequest};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::sync::watch;

/// C# `CliRunResult`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Ran {
    pub exit_code: i32,
    pub output: String,
    pub error_output: String,
    pub timed_out: bool,
}

/// Reads a pipe to its end, keeping at most `cap` bytes; `done` turns true at the end.
fn reader(pipe: Option<ChildReader>, cap: usize) -> (Arc<Mutex<Vec<u8>>>, watch::Receiver<bool>) {
    let kept = Arc::new(Mutex::new(Vec::new()));
    let (done, finished) = watch::channel(false);
    let sink = kept.clone();
    tokio::spawn(async move {
        if let Some(mut pipe) = pipe {
            let mut buffer = [0u8; 8192];
            loop {
                match pipe.read(&mut buffer).await {
                    Ok(0) | Err(_) => break,
                    Ok(read) => {
                        let mut kept = sink.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
                        let room = cap.saturating_sub(kept.len()).min(read);
                        kept.extend_from_slice(&buffer[..room]);
                    }
                }
            }
        }
        let _ = done.send(true);
    });
    (kept, finished)
}

fn text(bytes: &Arc<Mutex<Vec<u8>>>) -> String {
    String::from_utf8_lossy(&bytes.lock().unwrap_or_else(|poisoned| poisoned.into_inner())).into_owned()
}

/// Runs `request` to its end: `input` written (stdin closed either way), at most `timeout`, each output kept to `cap` bytes.
/// A child that cannot start is an `Err`; one past `timeout` is killed and answered `timed_out` with exit code -1. After the exit
/// the outputs get 500 ms (1 s after a kill) to end, as C#'s readers do, and whatever arrived is kept.
pub async fn run(processes: &dyn ProcessHost, request: SpawnRequest, input: Option<&[u8]>, timeout: Duration, cap: usize) -> Result<Ran> {
    let mut child = processes.spawn(request).await?;
    let control = child.control();
    let (out, mut out_done) = reader(child.stdout.take(), cap);
    let (err, mut err_done) = reader(child.stderr.take(), cap);
    if let Some(mut stdin) = child.stdin.take() {
        // A child that closed its stdin before reading all of it is no failure of the run.
        if let Some(input) = input {
            let _ = stdin.write_all(input).await;
        }
        let _ = stdin.shutdown().await;
    }
    let (exit_code, timed_out) = match tokio::time::timeout(timeout, control.wait()).await {
        Ok(Ok(code)) => (code, false),
        Ok(Err(error)) => {
            let _ = control.close().await;
            return Err(error);
        }
        Err(_) => {
            control.kill();
            (-1, true)
        }
    };
    let grace = Duration::from_millis(if timed_out { 1000 } else { 500 });
    let _ = tokio::time::timeout(grace, async {
        let _ = out_done.wait_for(|done| *done).await;
        let _ = err_done.wait_for(|done| *done).await;
    })
    .await;
    let _ = control.close().await;
    Ok(Ran { exit_code, output: text(&out), error_output: text(&err), timed_out })
}
