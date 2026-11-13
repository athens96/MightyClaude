//! C# `CliRunner` (CliRunner.cs) on Windows: runs a CLI to its end within a time limit and hands back its exit code and both
//! outputs as text, each kept to a cap while the pipe goes on being drained. A bare name is found along the PATH with PATHEXT and
//! a `.cmd` shim runs through cmd.exe ([`crate::command::find`], [`crate::command::launch`]); every line it logs passes one
//! redaction point.

use std::collections::BTreeMap;
use std::path::Path;
use std::sync::Arc;
use std::time::Duration;

use mighty_platform::process::ChildReader;
use mighty_platform::{ProcessHost, Result, ShellEnvironment};
use tokio::io::AsyncReadExt;
use tokio::sync::watch;

use crate::Variables;
use crate::command::{file_exists, find, launch};
use crate::process::CloseOnDrop;
use crate::text::TextDecoder;

/// 1 MiB cap on captured output — matches the macOS updater limit (CliRunner.cs:20-21).
pub const OUTPUT_CAP_BYTES: usize = 1024 * 1024;

/// C# `CliRunResult`: the child's exit code (-1 when the time limit killed it), its two outputs as text, and whether it timed out.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CliRunResult {
    pub exit_code: i32,
    pub output: String,
    pub error_output: String,
    pub timed_out: bool,
}

pub type LogSink = Arc<dyn Fn(&str) + Send + Sync>;
pub type Redactor = Arc<dyn Fn(&str) -> String + Send + Sync>;

/// Runs CLIs (C# `CliRunner`). Dropping a run's future half-way (C#'s cancelled token) kills the command and everything it
/// started, and closes it on the runtime.
pub struct CliRunner {
    host: Arc<dyn ProcessHost>,
    /// `CliEnvironment.Current()`: where a bare name is looked for when the run gives no environment of its own.
    environment: Arc<dyn ShellEnvironment>,
    log: Option<LogSink>,
    redact: Option<Redactor>,
    cap: usize,
}

impl CliRunner {
    pub fn new(host: Arc<dyn ProcessHost>, environment: Arc<dyn ShellEnvironment>) -> CliRunner {
        CliRunner { host, environment, log: None, redact: None, cap: OUTPUT_CAP_BYTES }
    }

    /// Every piece of text reaching `log` passes `redact` first (CliRunner.cs:23-27): secrets in arguments or output never reach
    /// a log unredacted.
    pub fn with_log(mut self, log: LogSink, redact: Option<Redactor>) -> CliRunner {
        self.log = Some(log);
        self.redact = redact;
        self
    }

    /// The cap on each output; 0 means the default (CliRunner.cs:34).
    pub fn with_output_cap(mut self, bytes: usize) -> CliRunner {
        self.cap = if bytes > 0 { bytes } else { OUTPUT_CAP_BYTES };
        self
    }

    /// The single redaction point (CliRunner.cs:37).
    fn log(&self, text: &str) {
        if let Some(log) = &self.log {
            match &self.redact {
                Some(redact) => log(&redact(text)),
                None => log(text),
            }
        }
    }

    /// C# `CliRunner.RunAsync` (CliRunner.cs:39-104): `working_directory` when it is an existing folder, else this process's;
    /// `environment` laid over the app's. A child that cannot start is an error; one that runs past `timeout` is killed with
    /// everything it started and reported `timed_out` with exit code -1 and what it had written.
    pub async fn run(
        &self,
        executable: &str,
        arguments: &[String],
        timeout: Duration,
        environment: Option<&BTreeMap<String, String>>,
        working_directory: Option<&Path>,
    ) -> Result<CliRunResult> {
        let cwd = match working_directory {
            Some(folder) if !folder.as_os_str().is_empty() && folder.is_dir() => folder.to_path_buf(),
            _ => std::env::current_dir()?,
        };
        self.log(&format!("run {executable} {}", arguments.join(" ")));
        // CreateProcessW looks along no PATH and runs no batch file: the full path is found here, and a .cmd shim runs through
        // cmd.exe. Neither cmd.exe running a shim nor the program itself looks in the working folder (a workspace) for a bare name.
        let lookup = match environment {
            Some(environment) => Variables::from(environment),
            None => Variables::from(&self.environment.current().values),
        };
        let resolved = find(executable, lookup.get("PATH"), lookup.get("PATHEXT"), &file_exists).unwrap_or_else(|| executable.to_string());
        let request = launch(&resolved, arguments, &cwd, environment, Some(&lookup))?;
        let mut child = self.host.spawn(request).await?;
        let guard = CloseOnDrop(Some(child.control()));
        let control = child.control();
        drop(child.stdin.take());

        // Both pipes are drained from the first instruction so a talkative child never blocks, and whatever arrived is kept even
        // when the run is killed.
        let output = spawn_reader(child.stdout.take(), self.cap);
        let error = spawn_reader(child.stderr.take(), self.cap);
        let outcome = async {
            let (exit_code, timed_out) = match tokio::time::timeout(timeout, control.wait()).await {
                Ok(code) => (code?, false),
                Err(_) => {
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
            let output = output.borrow().clone().unwrap_or_default();
            let error_output = error.borrow().clone().unwrap_or_default();
            self.log(&format!(
                "exit={exit_code} timedOut={} outBytes={} errBytes={}",
                if timed_out { "True" } else { "False" },
                output.len(),
                error_output.len()
            ));
            if !output.is_empty() {
                self.log(&format!("stdout {output}"));
            }
            if !error_output.is_empty() {
                self.log(&format!("stderr {error_output}"));
            }
            Ok(CliRunResult { exit_code, output, error_output, timed_out })
        }
        .await;
        // `finally { await process.DisposeAsync(); }`: an error of the close replaces the run's own, as an exception thrown in a
        // finally block does.
        let closed = guard.disarm().close().await;
        match closed {
            Err(error) => Err(error),
            Ok(()) => outcome,
        }
    }
}

/// Reads one output on a task of its own; the text appears in the channel once the pipe has ended (C#'s reader task).
fn spawn_reader(reader: Option<ChildReader>, cap: usize) -> watch::Receiver<Option<String>> {
    let (sender, receiver) = watch::channel(None);
    tokio::spawn(async move {
        let text = read_capped(reader, cap).await;
        sender.send_replace(Some(text));
    });
    receiver
}

/// Keeps at most `max_bytes` of text but goes on draining the pipe, so a child that writes more than the cap never blocks on a full
/// pipe and can still exit (C# `ReadCappedAsync`, CliRunner.cs:106-135). A read error ends the text where it is.
pub(crate) async fn read_capped(reader: Option<ChildReader>, max_bytes: usize) -> String {
    let Some(mut reader) = reader else { return String::new() };
    let mut decoder = TextDecoder::new();
    let mut kept = Capped { text: String::new(), bytes: 0, capped: false, max_bytes };
    let mut buffer = vec![0u8; 4096];
    loop {
        match reader.read(&mut buffer).await {
            Ok(0) | Err(_) => break,
            Ok(read) => {
                let chunk = decoder.push(&buffer[..read]);
                kept.add(&chunk);
            }
        }
    }
    kept.add(&decoder.finish());
    kept.text
}

struct Capped {
    text: String,
    bytes: usize,
    capped: bool,
    max_bytes: usize,
}

impl Capped {
    /// A chunk that does not fit whole adds the characters of it that fit, and nothing is added after.
    fn add(&mut self, chunk: &str) {
        if self.capped || chunk.is_empty() {
            return;
        }
        if self.bytes + chunk.len() > self.max_bytes {
            let room = self.max_bytes - self.bytes;
            let take = chunk.char_indices().map(|(at, c)| at + c.len_utf8()).take_while(|end| *end <= room).last().unwrap_or(0);
            self.text.push_str(&chunk[..take]);
            self.capped = true;
            return;
        }
        self.bytes += chunk.len();
        self.text.push_str(chunk);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_cap_keeps_whole_characters_and_nothing_after() {
        let mut kept = Capped { text: String::new(), bytes: 0, capped: false, max_bytes: 5 };
        kept.add("ab");
        kept.add("c한d");
        kept.add("e");
        assert_eq!(kept.text, "abc", "한 is three bytes and does not fit in the two left");
    }
}
