//! The two new ways to add a workspace (C# MainWindow.WorkspaceCreate.cs and `GitTool`/`GitCloneJob`, MightyCode.Core/WorkspaceCreate.cs;
//! the Mac's WorkspaceCreateViews.swift): a new folder, optionally `git init`, and a workspace cloned from a Git address. The page draws
//! the sheets (`newFolder`, `gitCreate`) and asks `createCheck`, `pickLocation`, `makeFolder`, `repoName`, `gitProbe`, `gitCreate` (its
//! progress pushed as `gitProgress`) and `gitCreateCancel`; every rule and word is mighty-core's `workspace_create`. git is the user's
//! own, with their configuration and credentials but never a prompt or a window, and runs through the platform's `ProcessHost` (a
//! clone's cancel ends git and everything it started, and removes what it wrote).

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use mighty_core::locale::Locale;
use mighty_core::workspace_create::{self as rules, CloneProgress, CreateCheck};
use mighty_platform::{ChildControl, ProcessHost, SpawnRequest};
use serde_json::{Value, json};
use tauri::Manager;
use tokio::io::AsyncReadExt;
use tokio::sync::watch;

use crate::dialogs::{self, without_nulls};
use crate::frame;
use crate::host::{Host, Registry};
use crate::methods::{flag, text};

/// The last 64 KiB of git's error text are kept (`KeptErrorChars`).
const KEPT_ERROR_CHARS: usize = 65_536;

/// C# `DelegationGit.Executable`: git on PATH (each folder trimmed of blanks and quotes, a full path, `git.exe` on Windows), the
/// first found kept while it is still there; looked for again at every call until one is (git installed while the app runs counts).
pub fn git_executable() -> Option<PathBuf> {
    static FOUND: Mutex<Option<PathBuf>> = Mutex::new(None);
    let mut found = FOUND.lock().unwrap_or_else(|p| p.into_inner());
    if found.as_ref().is_none_or(|git| !git.is_file()) {
        let name = if cfg!(windows) { "git.exe" } else { "git" };
        let path = std::env::var_os("PATH")?;
        *found = std::env::split_paths(&path)
            .map(|folder| PathBuf::from(folder.to_string_lossy().trim().trim_matches('"')))
            .filter(|folder| mighty_core::normalize::fully_qualified(&folder.to_string_lossy()))
            .map(|folder| folder.join(name))
            .find(|candidate| candidate.is_file());
    }
    found.clone()
}

/// A stop the caller can ask for (C#'s `CancellationToken`).
#[derive(Clone)]
pub struct Cancel(Arc<watch::Sender<bool>>);

impl Cancel {
    pub fn new() -> Cancel {
        Cancel(Arc::new(watch::channel(false).0))
    }
    pub fn cancel(&self) {
        let _ = self.0.send(true);
    }
    pub fn is_cancelled(&self) -> bool {
        *self.0.borrow()
    }
    async fn cancelled(&self) {
        let mut receiver = self.0.subscribe();
        let _ = receiver.wait_for(|stopped| *stopped).await;
    }
}

impl Default for Cancel {
    fn default() -> Cancel {
        Cancel::new()
    }
}

/// What a git run ended with (C# `GitResult`): its exit code and the end of its error text.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GitResult {
    pub exit_code: i32,
    pub stderr: String,
}

/// Why a git run did not end by itself.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum GitFailure {
    /// git never ran (it could not start).
    Start(String),
    /// Past its time: git and everything it started were killed.
    TimedOut,
    /// The caller stopped it.
    Cancelled,
}

/// C# `DelegationGit.Message`: git's error text trimmed (at most 500 characters), else its exit code.
pub fn git_message(result: &GitResult) -> String {
    let text = result.stderr.trim();
    if text.is_empty() { format!("Git exited with {}.", result.exit_code) } else { text.chars().take(500).collect() }
}

/// Reads to the end, keeping the last `keep` characters (a pipe always drained, so git never blocks on it).
async fn tail(reader: Option<mighty_platform::process::ChildReader>, keep: usize) -> String {
    let Some(mut reader) = reader else { return String::new() };
    let mut kept: Vec<u8> = Vec::new();
    let mut buffer = [0u8; 8192];
    while let Ok(read) = reader.read(&mut buffer).await {
        if read == 0 {
            break;
        }
        if keep > 0 {
            kept.extend_from_slice(&buffer[..read]);
            if kept.len() > keep * 4 {
                kept.drain(..kept.len() - keep * 4);
            }
        }
    }
    let text = String::from_utf8_lossy(&kept).into_owned();
    let count = text.chars().count();
    if count > keep { text.chars().skip(count - keep).collect() } else { text }
}

/// git's runs for making workspaces (C# `GitTool`), through the app's `ProcessHost`.
pub struct GitTool {
    processes: Arc<dyn ProcessHost>,
    git: Option<PathBuf>,
    /// `sshCommand`: the user's own core.sshCommand, once a look-up finished.
    ssh_command: Mutex<Option<Option<String>>>,
}

impl GitTool {
    pub fn new(processes: Arc<dyn ProcessHost>, git: Option<PathBuf>) -> GitTool {
        GitTool { processes, git, ssh_command: Mutex::new(None) }
    }

    pub fn executable(&self) -> Option<&Path> {
        self.git.as_deref()
    }

    /// C# `ApplyEnvironmentAsync`: no prompt can appear (an empty askpass, Git Credential Manager never interactive), the app's
    /// repository variables gone, and BatchMode ssh unless the user has an ssh command of their own.
    async fn environment(&self, request: SpawnRequest) -> SpawnRequest {
        let mut request = ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE"]
            .into_iter()
            .fold(request, |r, key| r.without(key))
            .env("GIT_TERMINAL_PROMPT", "0")
            .env("GCM_INTERACTIVE", "never")
            .env("LC_ALL", "C")
            .env("GIT_ASKPASS", "")
            .env("SSH_ASKPASS_REQUIRE", "never");
        let own = std::env::var_os("GIT_SSH_COMMAND").is_some() || std::env::var_os("GIT_SSH").is_some();
        if !own && self.ssh_command().await.is_none() {
            request = request.env("GIT_SSH_COMMAND", "ssh -o BatchMode=yes");
        }
        request
    }

    /// C# `SshCommandAsync`: `git config --get core.sshCommand` from home (no workspace's repository read); only a look-up that
    /// finished (set, or exit 1: not set) is kept.
    async fn ssh_command(&self) -> Option<String> {
        if let Some(known) = self.ssh_command.lock().unwrap_or_else(|p| p.into_inner()).clone() {
            return known;
        }
        let git = self.git.clone()?;
        let home = std::env::home_dir().filter(|home| home.is_dir()).unwrap_or_else(std::env::temp_dir);
        let request = ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE"]
            .into_iter()
            .fold(SpawnRequest::new(git, ["config", "--get", "core.sshCommand"], home), |r, key| r.without(key))
            .env("GIT_TERMINAL_PROMPT", "0")
            .env("LC_ALL", "C");
        let Ok(captured) = mighty_platform::process::capture(self.processes.as_ref(), request, None, Duration::from_secs(5), 65_536).await
        else {
            return None;
        };
        let value = String::from_utf8_lossy(&captured.stdout).trim().to_string();
        let (finished, value) = match captured.exit_code {
            0 if !value.is_empty() => (true, Some(value)),
            1 => (true, None),
            _ => (false, None),
        };
        if finished {
            *self.ssh_command.lock().unwrap_or_else(|p| p.into_inner()) = Some(value.clone());
        }
        value
    }

    /// C# `RunAsync`: standard input closed, standard output drained, the last 64 KiB of standard error kept; past `seconds` (or when
    /// `cancel` fires) git and everything it started are killed.
    pub async fn run(&self, arguments: &[&str], folder: &Path, seconds: u64, cancel: Option<&Cancel>) -> Result<GitResult, GitFailure> {
        let git = self.git.clone().ok_or_else(|| GitFailure::Start("git is missing".into()))?;
        let request = self.environment(SpawnRequest::new(git, arguments.iter().copied(), folder)).await;
        if cancel.is_some_and(Cancel::is_cancelled) {
            return Err(GitFailure::Cancelled);
        }
        let mut child = self.processes.spawn(request).await.map_err(|e| GitFailure::Start(e.to_string()))?;
        drop(child.stdin.take());
        let control = child.control();
        let (stdout, stderr) = (child.stdout.take(), child.stderr.take());
        let waiting = control.clone();
        let reads = tokio::spawn(async move { tokio::join!(tail(stdout, 0), tail(stderr, KEPT_ERROR_CHARS)) });
        let stopped = async {
            match cancel {
                Some(cancel) => cancel.cancelled().await,
                None => std::future::pending().await,
            }
        };
        let outcome = tokio::select! {
            exit = waiting.wait() => Ok(exit),
            _ = tokio::time::sleep(Duration::from_secs(seconds)) => Err(GitFailure::TimedOut),
            () = stopped => Err(GitFailure::Cancelled),
        };
        // A process git started can keep the pipes open after git exits: ending the job ends the reads too.
        control.kill();
        let read = tokio::time::timeout(Duration::from_secs(3), reads).await.ok().and_then(Result::ok);
        let _ = control.close().await;
        let exit_code = outcome?.unwrap_or(-1);
        Ok(GitResult { exit_code, stderr: read.map(|(_, stderr)| stderr).unwrap_or_default() })
    }

    /// C# `MakeFolderAsync`: the folder made (it must not exist yet while its parent does), with `git init` in it when asked:
    /// (`made` | `madeButGitInitFailed` | `failed`, the reason).
    pub async fn make_folder(&self, path: &Path, git_init: bool, locale: &Locale) -> (&'static str, Option<String>) {
        if path.exists() {
            return ("failed", Some(locale.get("workspace.name.error.exists")));
        }
        if !rules::parent_folder(path).is_some_and(|parent| parent.is_dir()) {
            return ("failed", Some(locale.get("workspace.location.error.missing")));
        }
        if let Err(error) = std::fs::create_dir(path) {
            return ("failed", Some(error.to_string()));
        }
        if !git_init {
            return ("made", None);
        }
        if self.git.is_none() {
            return ("madeButGitInitFailed", Some(locale.get("workspace.git.error.gitMissing")));
        }
        match self.run(&["init", "-q"], path, 20, None).await {
            Ok(result) if result.exit_code == 0 => ("made", None),
            Ok(result) => ("madeButGitInitFailed", Some(git_message(&result))),
            Err(GitFailure::TimedOut) => ("madeButGitInitFailed", Some(locale.get("process.error.timeout"))),
            Err(GitFailure::Start(error)) => ("madeButGitInitFailed", Some(error)),
            Err(GitFailure::Cancelled) => ("madeButGitInitFailed", None),
        }
    }

    /// C# `ProbeAsync`: whether this computer's own credentials reach the address (`git ls-remote -- <url> HEAD`, at most 20 s):
    /// (reachable, the error kind, git's text); none when `cancel` stopped it.
    pub async fn probe(&self, raw: &str, cancel: &Cancel, locale: &Locale) -> Option<(bool, Option<&'static str>, Option<String>)> {
        let url = raw.trim();
        if !rules::looks_like_git_url(url) {
            return Some((false, Some("invalidURL"), None));
        }
        if self.git.is_none() {
            return Some((false, Some("gitMissing"), None));
        }
        match self.run(&["ls-remote", "--", url, "HEAD"], &std::env::temp_dir(), 20, Some(cancel)).await {
            Ok(result) if result.exit_code == 0 => Some((true, None, None)),
            Ok(result) => Some((false, Some(rules::error_kind(&result.stderr)), Some(git_message(&result)))),
            Err(GitFailure::Cancelled) => None,
            Err(GitFailure::TimedOut) => Some((false, Some("network"), Some(locale.get("process.error.timeout")))),
            Err(GitFailure::Start(error)) => Some((false, Some("other"), Some(error))),
        }
    }
}

/// What a clone ended with (C# `CloneResult`): `done`, `cancelled` or `failed` with an error kind and git's last line.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CloneResult {
    pub outcome: &'static str,
    pub kind: Option<&'static str>,
    pub message: Option<String>,
}

impl CloneResult {
    fn failed(kind: &'static str, message: Option<String>) -> CloneResult {
        CloneResult { outcome: "failed", kind: Some(kind), message }
    }
    fn cancelled() -> CloneResult {
        CloneResult { outcome: "cancelled", kind: None, message: None }
    }
}

/// C# `GitCloneJob`: `git clone --progress` into a folder that must not exist yet; its progress from git's error text, a cancel that
/// stops it with every process it started, and nothing left behind by a cancelled or failed clone.
pub struct CloneJob {
    url: String,
    target: PathBuf,
    cancelled: AtomicBool,
    child: Mutex<Option<Arc<dyn ChildControl>>>,
}

impl CloneJob {
    pub fn new(url: &str, target: PathBuf) -> CloneJob {
        CloneJob { url: url.trim().to_string(), target, cancelled: AtomicBool::new(false), child: Mutex::new(None) }
    }

    /// Stops the clone, or keeps it from starting; `run` then removes what it wrote and answers `cancelled`.
    pub fn cancel(&self) {
        let child = self.child.lock().unwrap_or_else(|p| p.into_inner());
        self.cancelled.store(true, Ordering::SeqCst);
        if let Some(child) = child.as_ref() {
            child.kill();
        }
    }

    fn is_cancelled(&self) -> bool {
        self.cancelled.load(Ordering::SeqCst)
    }

    /// `RunAsync`; `progress` is called for each line of git's progress (a failing one never stops the reads).
    pub async fn run(&self, git: &GitTool, locale: &Locale, progress: impl Fn(CloneProgress) + Send + Sync + 'static) -> CloneResult {
        if self.is_cancelled() {
            return CloneResult::cancelled();
        }
        if !rules::looks_like_git_url(&self.url) {
            return CloneResult::failed("invalidURL", None);
        }
        let Some(executable) = git.executable() else { return CloneResult::failed("gitMissing", None) };
        // Refused before anything is touched, so a folder that was there already is never removed below.
        if self.target.exists() {
            return CloneResult::failed("other", Some(locale.get("workspace.name.error.exists")));
        }
        let Some(parent) = rules::parent_folder(&self.target).filter(|p| p.is_dir()) else {
            return CloneResult::failed("other", Some(locale.get("workspace.location.error.missing")));
        };
        let target = self.target.to_string_lossy().into_owned();
        let request = git.environment(SpawnRequest::new(executable, ["clone", "--progress", "--", &self.url, &target], &parent)).await;
        // git never ran, so there is nothing of its to remove.
        let mut child = match git.processes.spawn(request).await {
            Ok(child) => child,
            Err(error) => return CloneResult::failed("other", Some(error.to_string())),
        };
        let control = child.control();
        {
            let mut held = self.child.lock().unwrap_or_else(|p| p.into_inner());
            *held = Some(control.clone());
            if self.is_cancelled() {
                control.kill();
            }
        }
        drop(child.stdin.take());
        let (stdout, stderr) = (child.stdout.take(), child.stderr.take());
        let reads = tokio::spawn(async move { tokio::join!(tail(stdout, 0), read_progress(stderr, progress)) });
        let exit = control.wait().await.unwrap_or(-1);
        control.kill();
        let text =
            tokio::time::timeout(Duration::from_secs(5), reads).await.ok().and_then(Result::ok).map(|(_, text)| text).unwrap_or_default();
        *self.child.lock().unwrap_or_else(|p| p.into_inner()) = None;
        let _ = control.close().await;
        if exit == 0 && !self.is_cancelled() {
            return CloneResult { outcome: "done", kind: None, message: None };
        }
        delete_folder(&self.target).await;
        if self.is_cancelled() {
            return CloneResult::cancelled();
        }
        let last = text.rsplit('\n').next().unwrap_or("").to_string();
        CloneResult::failed(rules::error_kind(&text), Some(last))
    }
}

/// C# `ReadErrors`: git's error text split on `\r` and `\n` (git redraws its progress line with `\r`), each line's progress passed on;
/// the last 64 KiB kept, trimmed.
async fn read_progress(reader: Option<mighty_platform::process::ChildReader>, progress: impl Fn(CloneProgress)) -> String {
    let Some(mut reader) = reader else { return String::new() };
    let (mut line, mut all) = (Vec::<u8>::new(), String::new());
    let end_line = |line: &mut Vec<u8>, all: &mut String| {
        if line.is_empty() {
            return;
        }
        let text = String::from_utf8_lossy(line).into_owned();
        line.clear();
        all.push_str(&text);
        all.push('\n');
        if all.len() > KEPT_ERROR_CHARS {
            let cut = all.len() - KEPT_ERROR_CHARS / 2;
            let at = (cut..all.len()).find(|at| all.is_char_boundary(*at)).unwrap_or(all.len());
            all.drain(..at);
        }
        if let Some(step) = rules::progress(&text) {
            progress(step);
        }
    };
    let mut buffer = [0u8; 4096];
    while let Ok(read) = reader.read(&mut buffer).await {
        if read == 0 {
            break;
        }
        for byte in &buffer[..read] {
            if matches!(byte, b'\r' | b'\n') {
                end_line(&mut line, &mut all);
            } else {
                line.push(*byte);
            }
        }
    }
    all.push_str(&String::from_utf8_lossy(&line));
    all.trim().to_string()
}

/// C# `DeleteFolderAsync`: a folder a cancelled or failed clone left, removed with its read-only files (git marks its objects so,
/// which Windows does not delete) and without following a link inside; five tries 200 ms apart, as Windows may still hold a handle.
pub async fn delete_folder(path: &Path) {
    fn writable(path: &Path) {
        let Ok(entries) = std::fs::read_dir(path) else { return };
        for entry in entries.flatten() {
            let Ok(meta) = entry.path().symlink_metadata() else { continue };
            if meta.file_type().is_symlink() {
                continue;
            }
            if meta.is_dir() {
                writable(&entry.path());
            }
            let mut permissions = meta.permissions();
            if cfg!(windows) && permissions.readonly() {
                #[allow(clippy::permissions_set_readonly_false, reason = "Windows' read-only attribute, which only stops a delete")]
                permissions.set_readonly(false);
                let _ = std::fs::set_permissions(entry.path(), permissions);
            }
        }
    }
    for _ in 0..5 {
        if !path.exists() {
            return;
        }
        writable(path);
        if std::fs::remove_dir_all(path).is_ok() {
            return;
        }
        tokio::time::sleep(Duration::from_millis(200)).await;
    }
}

// ── the host's methods ──

/// A Git sheet's clone that runs: its job (a cancel stops it) and where its answer goes.
struct Running {
    job: Arc<CloneJob>,
    answer: watch::Receiver<Option<Value>>,
}

/// What a `gitCreate` for a sheet does now.
enum Turn {
    /// A clone runs in it: its answer is this call's too.
    Wait(watch::Receiver<Option<Value>>),
    /// Its clone ended done and the sheet is still open (its page loaded again before it heard): the same answer.
    Answer(Value),
    /// Nothing runs: this call clones.
    Start,
}

/// The Git sheets' clones (C# `gitJobs`): one at a time in each sheet, and the answer of one that ended done kept while its sheet
/// is open, so a page that loaded again during the clone gets it by asking again (its own call's answer went to the page before).
#[derive(Default)]
struct Clones {
    running: HashMap<u64, Running>,
    done: HashMap<u64, Value>,
}

impl Clones {
    fn turn(&self, dialog: u64) -> Turn {
        match (self.running.get(&dialog), self.done.get(&dialog)) {
            (Some(running), _) => Turn::Wait(running.answer.clone()),
            (None, Some(answer)) => Turn::Answer(answer.clone()),
            (None, None) => Turn::Start,
        }
    }

    fn start(&mut self, dialog: u64, job: Arc<CloneJob>) -> watch::Sender<Option<Value>> {
        let (sender, answer) = watch::channel(None);
        self.running.insert(dialog, Running { job, answer });
        sender
    }

    /// The clone ended with `answer`; a done one is kept while its sheet is `open`.
    fn finish(&mut self, dialog: u64, answer: &Value, open: bool) {
        self.running.remove(&dialog);
        if open && answer.get("outcome").and_then(Value::as_str) == Some("done") {
            self.done.insert(dialog, answer.clone());
        }
    }

    fn job(&self, dialog: u64) -> Option<Arc<CloneJob>> {
        self.running.get(&dialog).map(|running| running.job.clone())
    }

    /// The sheet is gone: its kept answer goes (a clone still running is cancelled by the caller and ends as `finish` says).
    fn forget(&mut self, dialog: u64) {
        self.done.remove(&dialog);
    }
}

/// The create sheets' state (C# `gitJobs`, `gitProbeCancel`).
#[derive(Default)]
struct Create {
    clones: Mutex<Clones>,
    probe: Mutex<Option<Cancel>>,
    git: Mutex<Option<Arc<GitTool>>>,
}

fn create(host: &Host) -> Arc<Create> {
    if let Some(create) = host.app.try_state::<Arc<Create>>() {
        return create.inner().clone();
    }
    host.app.manage(Arc::new(Create::default()));
    host.app.state::<Arc<Create>>().inner().clone()
}

/// The git tool for git as it is found now ([`git_executable`] at every call): the one kept is used again while it runs that git, so
/// git installed or removed while the app runs is seen at the next press.
fn git(host: &Host) -> Arc<GitTool> {
    let state = create(host);
    let mut kept = state.git.lock().unwrap_or_else(|p| p.into_inner());
    tool_for(&mut kept, host.processes.clone(), git_executable())
}

fn tool_for(kept: &mut Option<Arc<GitTool>>, processes: Arc<dyn ProcessHost>, found: Option<PathBuf>) -> Arc<GitTool> {
    match kept.as_ref() {
        Some(tool) if tool.executable() == found.as_deref() => tool.clone(),
        _ => kept.insert(Arc::new(GitTool::new(processes, found))).clone(),
    }
}

/// C# `CheckCreate` (Windows' name rules on every OS, as C#).
fn check(location: &str, name: &str, locale: &Locale) -> CreateCheck {
    rules::check_create(location, name, true, locale)
}

/// C# `CreateLocationDefault`: the last location used, else the active workspace's parent folder, else home.
fn location_default(host: &Host) -> String {
    let view = host.view();
    let state = &view.snapshot;
    let active = state.active_workspace_id.as_deref().and_then(|id| state.workspace(id)).filter(|w| w.remote.is_none()).map(|w| w.path());
    let home = std::env::home_dir().map(|home| home.to_string_lossy().into_owned()).unwrap_or_default();
    rules::default_location(state.last_create_location.as_deref(), active, &home)
}

/// C# `WorkspaceCreateLabels`: the sheets' words.
fn labels(git: bool, locale: &Locale) -> Value {
    json!({
        "name": locale.get("workspace.create.name"), "location": locale.get("workspace.create.location"),
        "choose": locale.get("workspace.create.chooseLocation"), "path": locale.get("workspace.create.path"),
        "gitInit": locale.get("workspace.newFolder.gitInit"),
        "create": locale.get(if git { "workspace.git.create" } else { "workspace.newFolder.create" }), "cancel": locale.get("workspace.create.cancel"),
        "url": locale.get("workspace.git.url"), "urlPlaceholder": locale.get("workspace.git.urlPlaceholder"), "test": locale.get("workspace.git.test"),
        "checking": locale.get("workspace.git.checking"), "reachable": locale.get("workspace.git.reachable"), "openSetup": locale.get("workspace.git.openSetup"),
        "starting": rules::progress_message(&CloneProgress::new("starting", 0, 0.0), locale),
    })
}

/// C# `ShowWebWorkspaceCreateAsync`: the sheet in the page; it answers `{choice:"done", path, location, notice?}`, `{choice:"openSetup"}`
/// or `{choice:"cancel"}`. A made folder is added as Ctrl+O adds one, its location remembered, a failed git init said in the banner.
pub async fn show(host: &Arc<Host>, kind: &str) -> Result<(), String> {
    let git_sheet = kind == mighty_core::add_pane::FROM_GIT;
    if !git_sheet && kind != mighty_core::add_pane::NEW_FOLDER {
        return Ok(());
    }
    let state = create(host);
    let result = {
        let Some(_sheet) = frame::Sheet::begin(host) else { return Ok(()) };
        let l = host.locale;
        let (id, answer) = dialogs::of(host).open(json!({
            "type": if git_sheet { "gitCreate" } else { "newFolder" },
            "title": l.get(if git_sheet { "workspace.git.title" } else { "workspace.newFolder.title" }),
            "subtitle": l.get(if git_sheet { "workspace.git.subtitle" } else { "workspace.newFolder.subtitle" }),
            "labels": labels(git_sheet, &l), "location": location_default(host),
        }));
        let result = answer.await.unwrap_or(Value::Null);
        // A sheet gone without its answer leaves no clone or check behind, nor a kept answer.
        {
            let mut clones = state.clones.lock().unwrap_or_else(|p| p.into_inner());
            if let Some(job) = clones.job(id) {
                job.cancel();
            }
            clones.forget(id);
        }
        if let Some(probe) = state.probe.lock().unwrap_or_else(|p| p.into_inner()).as_ref() {
            probe.cancel();
        }
        result
    };
    // "openSetup" opens 필요한 도구 at Git, which is W6's screen.
    if result.get("choice").and_then(Value::as_str) != Some("done") {
        return Ok(());
    }
    let (Some(path), Some(location)) = (result.get("path").and_then(Value::as_str), result.get("location").and_then(Value::as_str)) else {
        return Ok(());
    };
    if !Path::new(path).is_dir() {
        return Ok(());
    }
    let notice = result.get("notice").and_then(Value::as_str).map(str::to_string);
    // C# `FinishWorkspaceCreateAsync`.
    if host.view().snapshot.last_create_location.as_deref() != Some(location) {
        host.view().update(|state| state.last_create_location = Some(location.to_string()))?;
    }
    frame::open_workspace_folder(host, Path::new(path))?;
    if notice.is_some() {
        host.view().error = notice;
    }
    Ok(())
}

/// How a `gitCreate` begins.
enum Begin {
    /// Its answer at once: a kept one, or the check's refusal.
    Answer(Value),
    /// The running clone's answer, once it ends.
    Wait(watch::Receiver<Option<Value>>),
    /// This call's clone, registered: its job, its folder and where its answer goes.
    Started { job: Arc<CloneJob>, target: String, sender: watch::Sender<Option<Value>> },
}

/// What the sheet's clones say for a press, its clone registered under the same lock when one starts (no two at once).
fn begin_clone(state: &Create, dialog: u64, args: &Value, locale: &Locale) -> Begin {
    let mut clones = state.clones.lock().unwrap_or_else(|p| p.into_inner());
    match clones.turn(dialog) {
        Turn::Answer(answer) => return Begin::Answer(answer),
        Turn::Wait(answer) => return Begin::Wait(answer),
        Turn::Start => {}
    }
    let found = check(text(args, "location").unwrap_or(""), text(args, "name").unwrap_or(""), locale);
    if !found.can_create() {
        return Begin::Answer(without_nulls(json!({"outcome": "failed", "message": found.message})));
    }
    let job = Arc::new(CloneJob::new(text(args, "url").unwrap_or(""), PathBuf::from(&found.target)));
    let sender = clones.start(dialog, job.clone());
    Begin::Started { job, target: found.target, sender }
}

/// The `gitProgress` push of a clone in the sheet `dialog`.
fn push_progress(host: &Host, dialog: u64, step: &CloneProgress) {
    let message = json!({
        "kind": "gitProgress", "id": dialog, "phase": step.phase, "percent": step.percent, "fraction": step.fraction,
        "text": rules::progress_message(step, &host.locale),
    });
    let view = host.view();
    host.push(&view, "gitProgress", message.to_string());
}

pub fn register(methods: &mut Registry) {
    methods.add("workspaceCreate", |host: Arc<Host>, args| async move {
        let kind = text(&args, "kind").unwrap_or("").to_string();
        frame::act(&host, show(&host, &kind)).await
    });
    methods.add("createCheck", |host: Arc<Host>, args| async move {
        let found = check(text(&args, "location").unwrap_or(""), text(&args, "name").unwrap_or(""), &host.locale);
        Ok(json!({
            "problem": found.problem, "target": found.target, "exists": found.exists, "locationMissing": found.location_missing,
            "message": found.message,
        }))
        .map(without_nulls)
    });
    methods.add("pickLocation", |host: Arc<Host>, _| async move {
        Ok(frame::pick_folder_path(&host).await.map_or(Value::Null, |folder| json!(folder.to_string_lossy())))
    });
    methods.add("makeFolder", |host: Arc<Host>, args| async move {
        let (location, name) = (text(&args, "location").unwrap_or(""), text(&args, "name").unwrap_or(""));
        let found = check(location, name, &host.locale);
        if !found.can_create() {
            return Ok(without_nulls(json!({"outcome": "failed", "message": found.message, "path": found.target})));
        }
        let (outcome, message) = git(&host).make_folder(Path::new(&found.target), flag(&args, "gitInit"), &host.locale).await;
        let notice = (outcome == "madeButGitInitFailed")
            .then(|| host.locale.get_with("workspace.newFolder.gitInitFailed", &[("message", message.as_deref().unwrap_or(""))]));
        Ok(without_nulls(json!({"outcome": outcome, "message": message, "notice": notice, "path": found.target})))
    });
    methods.add("repoName", |_host: Arc<Host>, args| async move {
        Ok(rules::repo_name(text(&args, "url").unwrap_or("")).map_or(Value::Null, Value::String))
    });
    methods.add("gitProbe", |host: Arc<Host>, args| async move {
        // One check at a time: a newer address stops the older one, whose call answers `cancelled`.
        let state = create(&host);
        let cancel = Cancel::new();
        if let Some(older) = state.probe.lock().unwrap_or_else(|p| p.into_inner()).replace(cancel.clone()) {
            older.cancel();
        }
        let found = git(&host).probe(text(&args, "url").unwrap_or(""), &cancel, &host.locale).await;
        {
            let mut probe = state.probe.lock().unwrap_or_else(|p| p.into_inner());
            if probe.as_ref().is_some_and(|current| Arc::ptr_eq(&current.0, &cancel.0)) {
                *probe = None;
            }
        }
        Ok(match found {
            None => json!({"reachable": false, "kind": "cancelled"}),
            Some((reachable, kind, message)) => without_nulls(json!({
                "reachable": reachable, "kind": kind,
                "message": kind.map(|kind| rules::error_message(kind, message.as_deref(), &host.locale)),
            })),
        })
    });
    methods.add("gitCreate", |host: Arc<Host>, args| async move {
        // Only for a Git sheet that is open, one clone at a time in it: a second press (a page that loaded again during the clone)
        // gets the running clone's answer, or the answer of the one that ended done while the page was away.
        let Some(dialog) = args.get("dialogId").and_then(Value::as_f64).filter(|id| id.is_finite() && *id >= 0.0).map(|id| id as u64)
        else {
            return Ok(Value::Null);
        };
        if !dialogs::of(&host).is_open(dialog) {
            return Ok(Value::Null);
        }
        let state = create(&host);
        let (job, target, sender) = match begin_clone(&state, dialog, &args, &host.locale) {
            Begin::Answer(answer) => return Ok(answer),
            Begin::Wait(mut answer) => {
                let answer = answer.wait_for(Option::is_some).await.ok().and_then(|answer| answer.clone());
                return Ok(answer.unwrap_or(Value::Null));
            }
            Begin::Started { job, target, sender } => (job, target, sender),
        };
        push_progress(&host, dialog, &CloneProgress::new("starting", 0, 0.0));
        let reporter = host.clone();
        let result = job.run(&git(&host), &host.locale, move |step| push_progress(&reporter, dialog, &step)).await;
        let message = match result.kind {
            Some(kind) => Some(rules::error_message(kind, result.message.as_deref(), &host.locale)),
            None => result.message,
        };
        let answer = without_nulls(json!({
            "outcome": result.outcome, "kind": result.kind, "message": message,
            "path": (result.outcome == "done").then_some(target),
        }));
        let open = dialogs::of(&host).is_open(dialog);
        state.clones.lock().unwrap_or_else(|p| p.into_inner()).finish(dialog, &answer, open);
        let _ = sender.send(Some(answer.clone()));
        Ok(answer)
    });
    methods.add("gitCreateCancel", |host: Arc<Host>, args| async move {
        if let Some(dialog) = args.get("dialogId").and_then(Value::as_f64).filter(|id| id.is_finite() && *id >= 0.0)
            && let Some(job) = create(&host).clones.lock().unwrap_or_else(|p| p.into_inner()).job(dialog as u64)
        {
            job.cancel();
        }
        Ok(Value::Null)
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    /// git as found now: the kept tool while it runs that git, a new one when git appeared or went (a missing git is never kept for
    /// the app's run).
    #[test]
    fn the_git_tool_follows_git_as_it_is_found() {
        let processes: Arc<dyn ProcessHost> = Arc::new(mighty_platform::unsupported::Unsupported);
        let mut kept = None;
        let missing = tool_for(&mut kept, processes.clone(), None);
        assert!(missing.executable().is_none());
        let found = tool_for(&mut kept, processes.clone(), Some(PathBuf::from(r"C:\Git\cmd\git.exe")));
        assert_eq!(found.executable(), Some(Path::new(r"C:\Git\cmd\git.exe")), "git installed while the app runs is seen");
        assert!(Arc::ptr_eq(&found, &tool_for(&mut kept, processes.clone(), Some(PathBuf::from(r"C:\Git\cmd\git.exe")))), "kept");
        assert!(tool_for(&mut kept, processes, None).executable().is_none(), "git gone is seen too");
    }

    /// A page that loaded again during a clone asks again: while the clone runs it waits for that clone's answer, after it ended done
    /// (its sheet still open) it gets the same answer; a failed one leaves the next press to clone again; a closed sheet keeps nothing.
    #[tokio::test]
    async fn a_clone_answers_a_page_that_loaded_again() {
        let mut clones = Clones::default();
        assert!(matches!(clones.turn(7), Turn::Start));
        let job = Arc::new(CloneJob::new("https://example.com/a.git", PathBuf::from(r"C:\work\a")));
        let sender = clones.start(7, job.clone());
        assert!(clones.job(7).is_some_and(|running| Arc::ptr_eq(&running, &job)), "a cancel reaches the running clone");
        let Turn::Wait(mut waiting) = clones.turn(7) else { panic!("a clone runs") };
        let done = json!({"outcome": "done", "path": r"C:\work\a"});
        clones.finish(7, &done, true);
        let _ = sender.send(Some(done.clone()));
        assert_eq!(
            waiting.wait_for(Option::is_some).await.unwrap().clone(),
            Some(done.clone()),
            "the second press hears the first's answer"
        );
        assert!(matches!(clones.turn(7), Turn::Answer(answer) if answer == done), "and so does a press after it ended");
        clones.forget(7);
        assert!(matches!(clones.turn(7), Turn::Start), "the sheet gone, nothing is kept");
        let _ = clones.start(8, job.clone());
        clones.finish(8, &json!({"outcome": "failed", "kind": "notFound"}), true);
        assert!(matches!(clones.turn(8), Turn::Start), "after a failure the next press clones again");
        let _ = clones.start(9, job);
        clones.finish(9, &done, false);
        assert!(matches!(clones.turn(9), Turn::Start), "a done clone of a sheet already closed is not kept");
    }

    #[test]
    fn git_messages_are_its_text_or_its_exit() {
        assert_eq!(git_message(&GitResult { exit_code: 128, stderr: "  fatal: no\n".into() }), "fatal: no");
        assert_eq!(git_message(&GitResult { exit_code: 2, stderr: " ".into() }), "Git exited with 2.");
        assert_eq!(git_message(&GitResult { exit_code: 1, stderr: "x".repeat(900) }).len(), 500);
    }

    /// C# `WorkspaceCreateVerification.MakeFolderAndCloneWorkOnALocalBareRepository`, through the Windows `ProcessHost`.
    #[cfg(windows)]
    #[tokio::test(flavor = "multi_thread")]
    async fn make_folder_and_clone_work_on_a_local_bare_repository() {
        let Some(executable) = git_executable() else { return };
        let processes: Arc<dyn ProcessHost> = Arc::new(mighty_platform_windows::WindowsProcessHost::new());
        let root = std::env::temp_dir().join(format!("mighty-create-git-{}", uuid_like()));
        let work = root.join("work");
        std::fs::create_dir_all(&work).unwrap();
        let settings = root.join("fixture.gitconfig");
        std::fs::write(&settings, "").unwrap();
        // The fixture's own git, with no user or system configuration.
        let fixture = |folder: &Path, arguments: &[&str]| {
            let mut all = vec!["-C".to_string(), folder.to_string_lossy().into_owned()];
            all.extend(arguments.iter().map(|a| a.to_string()));
            let mut request = SpawnRequest::new(executable.clone(), all, folder);
            for (key, _) in std::env::vars() {
                if key.to_ascii_uppercase().starts_with("GIT_") {
                    request = request.without(key);
                }
            }
            request.env("GIT_CONFIG_GLOBAL", settings.to_string_lossy()).env("GIT_CONFIG_NOSYSTEM", "1").env("GIT_TERMINAL_PROMPT", "0")
        };
        let run = |request: SpawnRequest| {
            let processes = processes.clone();
            async move {
                let captured =
                    mighty_platform::process::capture(processes.as_ref(), request, None, Duration::from_secs(120), 1 << 20).await.unwrap();
                assert_eq!(captured.exit_code, 0, "{}", String::from_utf8_lossy(&captured.stderr));
            }
        };
        run(fixture(&work, &["init", "-q"])).await;
        std::fs::write(work.join("README.md"), "hello\n").unwrap();
        run(fixture(&work, &["add", "README.md"])).await;
        run(fixture(&work, &["-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-q", "-m", "first"])).await;
        let bare = root.join("remote.git");
        run(fixture(&root, &["clone", "-q", "--bare", &work.to_string_lossy(), &bare.to_string_lossy()])).await;

        let en = Locale::new("en");
        let tool = GitTool::new(processes.clone(), Some(executable.clone()));
        let reachable = tool.probe(&bare.to_string_lossy(), &Cancel::new(), &en).await.unwrap();
        assert!(reachable.0, "the bare repository is reachable: {reachable:?}");
        let missing = tool.probe(&root.join("none.git").to_string_lossy(), &Cancel::new(), &en).await.unwrap();
        assert_eq!((missing.0, missing.1), (false, Some("notFound")), "a missing repository is notFound: {missing:?}");

        let copy = root.join("copy");
        let steps = Arc::new(Mutex::new(vec![]));
        let seen = steps.clone();
        let done = CloneJob::new(&bare.to_string_lossy(), copy.clone()).run(&tool, &en, move |s| seen.lock().unwrap().push(s)).await;
        assert!(done.outcome == "done" && copy.join("README.md").is_file(), "the clone is done with README.md: {done:?}");
        assert!(steps.lock().unwrap().iter().all(|s| (0.0..=1.0).contains(&s.fraction)), "every step within 0 and 1");

        let not_started = root.join("cancelled");
        let job = CloneJob::new(&bare.to_string_lossy(), not_started.clone());
        job.cancel();
        let cancelled = job.run(&tool, &en, |_| {}).await;
        assert!(cancelled.outcome == "cancelled" && !not_started.exists(), "a clone cancelled before it runs leaves no folder");

        let again = CloneJob::new(&bare.to_string_lossy(), copy.clone()).run(&tool, &en, |_| {}).await;
        assert!(again.outcome == "failed" && copy.join("README.md").is_file(), "a clone into an existing folder fails and leaves it");

        // As a file:// address, so git answers through its transport (and makes the folder first) as for a server.
        let failed_target = root.join("failed");
        let missing_url = format!("file:///{}", root.join("none.git").to_string_lossy().replace('\\', "/").trim_start_matches('/'));
        let failed = CloneJob::new(&missing_url, failed_target.clone()).run(&tool, &en, |_| {}).await;
        assert!(
            failed.outcome == "failed" && failed.kind == Some("notFound") && !failed_target.exists(),
            "a clone of a missing repository fails as notFound and leaves no folder: {failed:?}"
        );

        let repo = root.join("repo");
        let made = tool.make_folder(&repo, true, &en).await;
        assert!(made.0 == "made" && repo.join(".git").is_dir(), "the new folder is a Git repository: {made:?}");
        assert_eq!(tool.make_folder(&repo, false, &en).await.0, "failed", "an existing folder is never made again");
        delete_folder(&root).await;
        assert!(!root.exists(), "the clones' read-only objects are removed too");
    }

    #[cfg(windows)]
    fn uuid_like() -> String {
        format!("{}-{}", std::process::id(), std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos())
    }
}
