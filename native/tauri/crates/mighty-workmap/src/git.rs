//! Git as the work map runs it (C# `DelegationGit.Run`, DelegationWorktree.cs:188-291): git from the PATH through the platform's
//! `ProcessHost`, the user's own settings but no hooks, fsmonitor or auto gc, no program a repository's config names ([`no_programs`]:
//! no signature check, no transport), no inherited `GIT_*` variable, no prompt, at most 1 MiB of output and a deadline. The work map's
//! commands only read.

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Duration;

use mighty_platform::process::ChildReader;
use mighty_platform::{ProcessHost, SpawnRequest};
use tokio::io::AsyncReadExt;

/// `DelegationGit.MaximumBytes`.
pub const MAXIMUM_BYTES: usize = 1_048_576;

/// `GitResult(ExitCode, Stdout, Stderr)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GitResult {
    pub exit_code: i32,
    pub stdout: String,
    pub stderr: String,
}

/// `DelegationGit.Line`.
pub fn line(output: &str) -> &str {
    output.trim()
}

/// `DelegationGit.Executable`: git on the PATH (a relative PATH entry names no place, so the folder a command runs in never decides).
pub fn executable() -> Option<PathBuf> {
    let path = std::env::var_os("PATH")?;
    let name = if mighty_run::os::windows() { "git.exe" } else { "git" };
    std::env::split_paths(&path)
        .map(|folder| PathBuf::from(folder.to_string_lossy().trim().trim_matches('"')))
        .filter(|folder| mighty_core::normalize::fully_qualified(&folder.to_string_lossy()))
        .map(|folder| folder.join(name))
        .find(|candidate| candidate.is_file())
}

/// The settings that keep a repository's own config from running a program while git only reads it, after the user's and the
/// repository's settings (a `-c` wins over both): no signature is checked (`log.showSignature`), the signature programs name a path
/// under the git executable itself, which is a file, so nothing can ever be there to run (`gpg.program`, `gpg.ssh.program`,
/// `gpg.x509.program`), and no transport is allowed, so a partial clone never fetches what it lacks through a remote's command
/// (`protocol.allow`, with `GIT_NO_LAZY_FETCH`). The header's `git status` (src-tauri host.rs `read_git`) passes the same.
pub fn no_programs(git: &Path) -> Vec<String> {
    let nothing = git.join("no-program").to_string_lossy().into_owned();
    let mut settings = vec!["-c".to_string(), "log.showSignature=false".to_string()];
    for name in ["gpg.program", "gpg.ssh.program", "gpg.x509.program"] {
        settings.extend(["-c".to_string(), format!("{name}={nothing}")]);
    }
    settings.extend(["-c".to_string(), "protocol.allow=never".to_string()]);
    settings
}

/// Git for one app: the process host every child starts through, and the empty folder git is pointed at for hooks.
#[derive(Clone)]
pub struct Git {
    processes: Arc<dyn ProcessHost>,
    hooks: PathBuf,
    program: Option<PathBuf>,
}

impl Git {
    /// `hooks`: an empty folder of the app's own (`DelegationGit.NoHooks`; on Windows git reads a device name such as `NUL` as a
    /// relative path, so a real folder is named). Checked empty before each run.
    pub fn new(processes: Arc<dyn ProcessHost>, hooks: PathBuf) -> Git {
        Git { processes, hooks, program: executable() }
    }

    /// Whether git was found.
    pub fn available(&self) -> bool {
        self.program.is_some()
    }

    /// `HooksEmpty`: the hooks folder is a plain, empty folder, made when it is missing. Git does not run while it is not.
    fn hooks_empty(&self) -> bool {
        if !mighty_run::os::windows() && self.hooks == Path::new("/dev/null") {
            return true;
        }
        if std::fs::create_dir_all(&self.hooks).is_err() {
            return false;
        }
        let plain = std::fs::symlink_metadata(&self.hooks).is_ok_and(|m| m.is_dir() && !m.file_type().is_symlink());
        plain && std::fs::read_dir(&self.hooks).is_ok_and(|mut entries| entries.next().is_none())
    }

    /// `DelegationGit.Run`: None when git is missing, could not start, wrote more than 1 MiB, did not finish in time, or the hooks folder
    /// is not empty.
    pub async fn run(&self, arguments: &[&str], directory: &Path, timeout: f64) -> Option<GitResult> {
        let program = self.program.clone()?;
        if !self.hooks_empty() {
            return None;
        }
        let hooks = format!("core.hooksPath={}", self.hooks.display());
        let folder = directory.to_string_lossy().into_owned();
        let mut args: Vec<String> = ["-c", "core.fsmonitor=false", "-c", &hooks, "-c", "gc.auto=0"].iter().map(|s| s.to_string()).collect();
        if mighty_run::os::windows() {
            args.extend(["-c".to_string(), "core.longpaths=true".to_string()]);
        }
        args.extend(no_programs(&program));
        args.extend(["-C".to_string(), folder]);
        args.extend(arguments.iter().map(|a| a.to_string()));
        let mut request = SpawnRequest::new(program, args, directory);
        for (key, _) in std::env::vars_os() {
            let key = key.to_string_lossy().into_owned();
            if key.to_ascii_uppercase().starts_with("GIT_") {
                request = request.without(key);
            }
        }
        let request = request
            .env("GIT_OPTIONAL_LOCKS", "0")
            .env("GIT_TERMINAL_PROMPT", "0")
            .env("LC_ALL", "C")
            .env("GIT_NO_LAZY_FETCH", "1");
        let mut child = self.processes.spawn(request).await.ok()?;
        drop(child.stdin.take());
        let control = child.control();
        let (stdout, stderr) = (child.stdout.take(), child.stderr.take());
        let total = Arc::new(AtomicUsize::new(0));
        async fn read(reader: Option<ChildReader>, total: Arc<AtomicUsize>) -> Option<Vec<u8>> {
            let mut kept = vec![];
            let Some(mut reader) = reader else { return Some(kept) };
            let mut buffer = [0u8; 8192];
            loop {
                let read = reader.read(&mut buffer).await.ok()?;
                if read == 0 {
                    return Some(kept);
                }
                if total.fetch_add(read, Ordering::SeqCst) + read > MAXIMUM_BYTES {
                    return None;
                }
                kept.extend_from_slice(&buffer[..read]);
            }
        }
        let waiting = control.clone();
        let outcome = tokio::time::timeout(Duration::from_secs_f64(timeout.max(0.01)), async move {
            let (out, err) = tokio::join!(read(stdout, total.clone()), read(stderr, total));
            let exit = waiting.wait().await.ok()?;
            Some(GitResult {
                exit_code: exit,
                stdout: String::from_utf8_lossy(&out?).into_owned(),
                stderr: String::from_utf8_lossy(&err?).into_owned(),
            })
        })
        .await;
        // Ending the job here is the app's own clean-up, not a timeout.
        control.kill();
        let _ = control.close().await;
        outcome.ok().flatten()
    }
}
