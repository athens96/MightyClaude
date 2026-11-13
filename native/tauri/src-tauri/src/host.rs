//! The page's host (C# `WebBridge`, MightyCode.WinUI/WebBridge.cs, and the window state `WebShellModel` reads): page → host
//! messages arrive through one command, `host_post`; every host → page message — a call's result and every push — leaves through
//! one Tauri event, `host-message`, from one thread, so they reach the page in the order they were posted (a push posted while a
//! call is handled arrives before its result).
//!
//! - A call `{kind:"call", id, method, args?}` is answered `{kind:"result", id, ok, value?, error?}` (a null value or error left
//!   out). Calls are handled one at a time, in order. A method this host does not have yet answers
//!   `ok:false, error:"unimplemented: <method>"`; its name is logged once and kept for `tauri-logs/unimplemented.json`.
//! - Pushes are dropped until the page has called `ready`, which pushes the whole snapshot before its result; a page that loads
//!   again starts over. A change is sent as a `delta` (`delta.rs`, C# `WebDelta`) at most 16 ms after it, coalesced with the
//!   others of that frame (`Bridge.Flush`'s timer), and a 100 ms poll catches a change made outside a call; the frame model
//!   (`shell`) is pushed when its JSON changed, looked at after every flush and every 250 ms (its relative times move on).
//! - Every change goes through [`View::update`] (C# `DesktopService.UpdateAsync`): normalized as C# normalizes it, then queued
//!   for saving (`store::Saver`). The store writes only when the app was started with `--allow-state-write`.

use std::collections::{BTreeSet, HashMap};
use std::future::Future;
use std::path::PathBuf;
use std::pin::Pin;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, mpsc};
use std::time::Duration;

use mighty_core::AppSnapshot;
use mighty_core::locale::Locale;
use mighty_core::model::PaneLayoutNode;
use mighty_core::normalize;
use mighty_core::shell::{Attention, RemoteConnection, Runtime, ShellContext, ShellModel, WorkspaceGitInfo, shell_model};
use mighty_core::store::{Saver, Store};
use serde_json::value::RawValue;
use serde_json::{Map, Value, json};
use tauri::{AppHandle, Emitter};

use crate::delta::Bridge;
use crate::logfile::LogFile;
use crate::smoke::Smoke;

/// The event every host → page message travels on, and the window it goes to.
pub const EVENT: &str = "host-message";
pub const WINDOW: &str = "main";

pub type Reply = Result<Value, String>;
type Pending = Pin<Box<dyn Future<Output = Reply> + Send>>;
type Handler = Box<dyn Fn(Arc<Host>, Value) -> Pending + Send + Sync>;

/// The methods the page may call, by name.
#[derive(Default)]
pub struct Registry {
    handlers: HashMap<&'static str, Handler>,
}

impl Registry {
    pub fn add<F, Fut>(&mut self, name: &'static str, handler: F)
    where
        F: Fn(Arc<Host>, Value) -> Fut + Send + Sync + 'static,
        Fut: Future<Output = Reply> + Send + 'static,
    {
        self.handlers.insert(name, Box::new(move |host, args| Box::pin(handler(host, args))));
    }

    pub fn names(&self) -> BTreeSet<&'static str> {
        self.handlers.keys().copied().collect()
    }
}

/// The state the page sees, what was last sent of it, and the window's own state the frame model reads.
pub struct View {
    pub snapshot: AppSnapshot,
    /// Bumped by every change; a flush of a revision already sent sends nothing (C#: the snapshot's reference).
    revision: u64,
    pub ready: bool,
    bridge: Bridge,
    sent_shell: Option<String>,
    /// The window's `layoutDefaults`: layouts made for workspaces without a saved one, kept so their ids stay put.
    pub layout_defaults: HashMap<String, PaneLayoutNode>,
    pub shows_dashboard: bool,
    pub dialog_open: bool,
    /// The error banner (C#'s `error.Text`).
    pub error: Option<String>,
    /// The active workspace's git: the key it was read for (`id|path`) and what it said.
    git: Option<(String, Option<WorkspaceGitInfo>)>,
    saver: Option<Arc<Saver>>,
}

impl View {
    /// C# `DesktopService.UpdateAsync`: the change, normalized as C# normalizes every change, then saved (queued).
    pub fn update(&mut self, change: impl FnOnce(&mut AppSnapshot)) -> Result<(), String> {
        let mut next = self.snapshot.clone();
        change(&mut next);
        let next = normalize::normalize(&next, false).map_err(|e| e.to_string())?;
        self.snapshot = next;
        self.revision += 1;
        if let Some(saver) = &self.saver {
            saver.queue(self.snapshot.clone());
        }
        Ok(())
    }

    /// The state replaced as it is, without a normalize or a save (a change C# makes to `snapshot` directly).
    pub fn replace(&mut self, next: AppSnapshot) {
        self.snapshot = next;
        self.revision += 1;
    }
}

/// One message for the page: its text, and its kind for the smoke's record.
struct Outgoing {
    kind: String,
    json: String,
}

pub struct Host {
    pub app: AppHandle,
    pub data_dir: PathBuf,
    pub locale: Locale,
    pub version: String,
    pub log: LogFile,
    pub smoke: Option<Smoke>,
    view: Arc<Mutex<View>>,
    saver: Arc<Saver>,
    registry: Registry,
    outbox: Mutex<mpsc::Sender<Outgoing>>,
    unimplemented: Mutex<BTreeSet<String>>,
    /// A flush is waiting on its 16 ms timer.
    flush_pending: AtomicBool,
    closed: AtomicBool,
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

impl Host {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        app: AppHandle,
        data_dir: PathBuf,
        store: Store,
        locale: Locale,
        version: String,
        banner: Option<String>,
        log: LogFile,
        smoke: Option<Smoke>,
        registry: Registry,
    ) -> Arc<Host> {
        let (outbox, outgoing) = mpsc::channel::<Outgoing>();
        let emitter = app.clone();
        // One thread posts every message, in order.
        std::thread::Builder::new()
            .name("host-message".into())
            .spawn(move || {
                for message in outgoing {
                    match RawValue::from_string(message.json) {
                        Ok(raw) => {
                            let _ = emitter.emit_to(WINDOW, EVENT, &raw);
                        }
                        Err(error) => eprintln!("a {} message is no JSON: {error}", message.kind),
                    }
                }
            })
            .expect("the host-message thread");
        let snapshot = store.snapshot.clone();
        let store = Arc::new(Mutex::new(store));
        let view = Arc::new(Mutex::new(View {
            snapshot,
            revision: 0,
            ready: false,
            bridge: Bridge::default(),
            sent_shell: None,
            layout_defaults: HashMap::new(),
            shows_dashboard: false,
            dialog_open: false,
            error: banner,
            git: None,
            saver: None,
        }));
        // Saves: the state as it is now when a scheduled one fires; written by the store (which writes nothing unless allowed).
        let (current, writer, failed_view, failed_log) = (view.clone(), store.clone(), view.clone(), LogFile::new(log.dir().to_path_buf()));
        let failed_locale = locale;
        let saver = Arc::new(Saver::new(
            move || lock(&current).snapshot.clone(),
            move |snapshot| lock(&writer).save(snapshot),
            move |error| {
                if matches!(error, mighty_core::store::StateError::WritesOff) {
                    return;
                }
                failed_log.line(&format!("save failed: {error}"));
                // window.error.saveFailed, as C#'s PersistenceFailed shows it.
                let text = failed_locale.get_with("window.error.saveFailed", &[("reason", &error.message(&failed_locale))]);
                lock(&failed_view).error = Some(text);
            },
        ));
        lock(&view).saver = Some(saver.clone());
        Arc::new(Host {
            app,
            data_dir,
            locale,
            version,
            log,
            smoke,
            view,
            saver,
            registry,
            outbox: Mutex::new(outbox),
            unimplemented: Mutex::new(BTreeSet::new()),
            flush_pending: AtomicBool::new(false),
            closed: AtomicBool::new(false),
        })
    }

    pub fn view(&self) -> MutexGuard<'_, View> {
        lock(&self.view)
    }

    /// Posts a message whatever the page's state (a call's result).
    fn post(&self, kind: &str, json: String) {
        let _ = lock(&self.outbox).send(Outgoing { kind: kind.to_string(), json });
    }

    /// Posts a push, or drops it when the page has not called `ready` since it loaded.
    fn push(&self, view: &View, kind: &str, json: String) {
        if !view.ready {
            return;
        }
        if let Some(smoke) = &self.smoke {
            smoke.pushed(kind);
        }
        self.post(kind, json);
    }

    /// The page is loading (again): it gets nothing until it calls `ready`.
    pub fn page_started(&self) {
        let mut view = self.view();
        view.ready = false;
        view.bridge.reset();
        view.sent_shell = None;
    }

    /// `ready`: pushes the whole snapshot (before the call's result) and turns pushes on.
    pub fn ready(&self) {
        let mut view = self.view();
        view.ready = true;
        let revision = view.revision;
        let View { snapshot, bridge, .. } = &mut *view;
        let text = bridge.snapshot(snapshot, revision);
        self.push(&view, "snapshot", text);
    }

    pub fn shell_model(&self, view: &mut View) -> ShellModel {
        let empty_attention: HashMap<String, Attention> = HashMap::new();
        let empty_git: HashMap<String, WorkspaceGitInfo> = HashMap::new();
        let empty_remote: HashMap<String, RemoteConnection> = HashMap::new();
        let View { snapshot, layout_defaults, shows_dashboard, dialog_open, error, git, .. } = view;
        let active_git =
            active_git_key(snapshot).and_then(|key| git.as_ref().filter(|(k, _)| *k == key).and_then(|(_, info)| info.as_ref()));
        let context = ShellContext {
            locale: self.locale,
            version: &self.version,
            now: chrono::Utc::now(),
            error: error.clone(),
            resource: None,
            dialog_open: *dialog_open,
            shows_dashboard: *shows_dashboard,
            attention: &empty_attention,
            git: active_git,
            dashboard_git: &empty_git,
            children: &[],
            // No runtime check yet (shell::Pending::Runtime): the machine at rest.
            runtime: Runtime::Known,
            setup_badge: false,
            setup_needed: false,
            remote: &empty_remote,
        };
        shell_model(snapshot, &context, layout_defaults)
    }

    /// The frame model, pushed when its JSON changed (`PushWebShellModel`).
    fn push_shell(&self, view: &mut View) {
        if !view.ready {
            view.sent_shell = None;
            return;
        }
        let model = self.shell_model(view);
        let text = serde_json::to_string(&model).unwrap_or_default();
        if view.sent_shell.as_deref() == Some(text.as_str()) {
            return;
        }
        view.sent_shell = Some(text.clone());
        self.push(view, "shell", format!("{{\"kind\":\"shell\",\"shell\":{text}}}"));
    }

    /// Sends what changed now: the delta (`Flush`), then the frame model when it changed.
    pub fn flush(&self) {
        let mut view = self.view();
        if !view.ready {
            return;
        }
        let revision = view.revision;
        let View { snapshot, bridge, .. } = &mut *view;
        if let Some(delta) = bridge.flush(snapshot, revision) {
            self.push(&view, "delta", delta);
        }
        self.push_shell(&mut view);
    }

    /// `Schedule`: a flush about one frame from now, which every change until then rides along with.
    pub fn schedule(self: &Arc<Self>) {
        if self.flush_pending.swap(true, Ordering::SeqCst) {
            return;
        }
        let host = self.clone();
        std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(16));
            host.flush_pending.store(false, Ordering::SeqCst);
            host.flush();
        });
    }

    /// The bridge's 100 ms poll (a change made outside a call), the frame's 250 ms clock and the active workspace's git.
    pub fn start_clocks(self: &Arc<Self>) {
        let host = self.clone();
        std::thread::Builder::new()
            .name("bridge-poll".into())
            .spawn(move || {
                let mut ticks = 0u64;
                while !host.closed.load(Ordering::SeqCst) {
                    std::thread::sleep(Duration::from_millis(100));
                    ticks += 1;
                    let (ready, changed) = {
                        let view = host.view();
                        (view.ready, view.bridge.sent_revision() != Some(view.revision))
                    };
                    if ready && changed {
                        host.schedule();
                    }
                    if ticks.is_multiple_of(5) {
                        host.refresh_git(ticks.is_multiple_of(100));
                    }
                }
            })
            .expect("the bridge poll");
        let host = self.clone();
        std::thread::Builder::new()
            .name("shell-clock".into())
            .spawn(move || {
                while !host.closed.load(Ordering::SeqCst) {
                    std::thread::sleep(Duration::from_millis(250));
                    let mut view = host.view();
                    host.push_shell(&mut view);
                }
            })
            .expect("the shell clock");
    }

    /// C# `RefreshGitInfo`: the active local workspace's git read once for its folder (again when `force`); a remote workspace's
    /// is its host's and is never read here.
    fn refresh_git(self: &Arc<Self>, force: bool) {
        let (key, folder) = {
            let mut view = self.view();
            let Some(key) = active_git_key(&view.snapshot) else {
                view.git = None;
                return;
            };
            if view.git.as_ref().is_some_and(|(k, _)| *k == key) && !force {
                return;
            }
            let folder = key.split_once('|').map(|(_, path)| path.to_string()).unwrap_or_default();
            if view.git.as_ref().is_none_or(|(k, _)| *k != key) {
                view.git = Some((key.clone(), None));
            }
            (key, folder)
        };
        let host = self.clone();
        std::thread::spawn(move || {
            let info = read_git(&folder);
            let mut view = host.view();
            if active_git_key(&view.snapshot).as_deref() == Some(key.as_str()) {
                view.git = Some((key, info));
            }
        });
    }

    /// The last save, then the saver's end (C# `DesktopService.DisposeAsync`). Writes only when the store may.
    pub fn close(&self) {
        if self.closed.swap(true, Ordering::SeqCst) {
            return;
        }
        let last = self.view().snapshot.clone();
        self.saver.close(last);
    }

    fn unimplemented(&self, method: &str) {
        if lock(&self.unimplemented).insert(method.to_string()) {
            self.log.line(&format!("unimplemented: {method}"));
        }
    }

    pub fn unimplemented_methods(&self) -> Vec<String> {
        lock(&self.unimplemented).iter().cloned().collect()
    }

    /// Adds this run's unimplemented methods to `tauri-logs/unimplemented.json` (the porting checklist, kept across runs).
    pub fn write_unimplemented(&self) {
        let path = self.log.dir().join("unimplemented.json");
        let mut all: BTreeSet<String> = std::fs::read(&path)
            .ok()
            .and_then(|bytes| serde_json::from_slice::<Value>(&bytes).ok())
            .and_then(|v| v.get("methods").and_then(Value::as_array).cloned())
            .map(|list| list.iter().filter_map(|m| m.as_str().map(str::to_string)).collect())
            .unwrap_or_default();
        all.extend(self.unimplemented_methods());
        let body = json!({
            "about": "Page methods the Rust host answered with \"unimplemented\", every run together: the porting checklist.",
            "updatedAt": chrono::Utc::now().to_rfc3339(),
            "implemented": self.registry.names(),
            "methods": all,
        });
        let _ = std::fs::create_dir_all(self.log.dir());
        if let Err(error) = std::fs::write(&path, serde_json::to_vec_pretty(&body).unwrap_or_default()) {
            self.log.line(&format!("cannot write {}: {error}", path.display()));
        }
    }

    /// One page → host message, handled to the end before the next (see the module comment).
    pub async fn dispatch(self: Arc<Self>, message: Value) {
        if message.get("kind").and_then(Value::as_str) != Some("call") {
            return;
        }
        let (Some(id), Some(method)) =
            (message.get("id").filter(|id| id.is_number()).cloned(), message.get("method").and_then(Value::as_str))
        else {
            self.log.line("a call without an id or a method was ignored");
            return;
        };
        let args = message.get("args").cloned().unwrap_or(Value::Null);
        if let Some(smoke) = &self.smoke {
            smoke.called(method);
        }
        let reply = match self.registry.handlers.get(method) {
            Some(handler) => handler(self.clone(), args).await,
            None => {
                self.unimplemented(method);
                Err(format!("unimplemented: {method}"))
            }
        };
        let mut result = Map::new();
        result.insert("kind".into(), json!("result"));
        result.insert("id".into(), id);
        match reply {
            Ok(value) => {
                result.insert("ok".into(), json!(true));
                if !value.is_null() {
                    result.insert("value".into(), value);
                }
            }
            Err(error) => {
                result.insert("ok".into(), json!(false));
                result.insert("error".into(), json!(error));
            }
        }
        self.post("result", Value::Object(result).to_string());
        // A call usually changes the state: the page sees it within a frame instead of at the next poll.
        self.schedule();
        if let Some(smoke) = &self.smoke {
            smoke.check(&self);
        }
    }
}

/// The active workspace's git key (`id|folder`), for a local one.
fn active_git_key(state: &AppSnapshot) -> Option<String> {
    let workspace = state.active_workspace_id.as_deref().and_then(|id| state.workspace(id))?;
    (workspace.remote.is_none() && !workspace.path().is_empty()).then(|| format!("{}|{}", workspace.id(), workspace.path()))
}

/// C# `WorkspaceGitInfo.ReadAsync`: `git status --porcelain=v2 --branch` in the folder, locks, hooks and fsmonitor off, the
/// `GIT_*` environment cleared, at most 2 s and 256 KB; None when git is not on PATH, the folder is gone or git fails.
///
/// The one spawn of the app that does not go through `Platform::processes` yet (README: Child processes): C# starts git with
/// `ChildProcess.Start`, but the port cannot remove the app's `GIT_*` variables (its environment is laid over the app's) and the
/// Mac's `ProcessHost` is still `Unsupported`. Until it moves, a git read that coincides with a Windows launch can hold that
/// child's pipe ends open for as long as git runs (2 s at most).
#[allow(clippy::disallowed_methods, reason = "the one named exception to the ProcessHost rule, until git reads move onto the port")]
pub fn read_git(folder: &str) -> Option<WorkspaceGitInfo> {
    use std::io::Read;
    use std::process::{Command, Stdio};
    if !normalize::fully_qualified(folder) || !std::path::Path::new(folder).is_dir() {
        return None;
    }
    let name = if cfg!(windows) { "git.exe" } else { "git" };
    let path = std::env::var_os("PATH")?;
    let binary = std::env::split_paths(&path).map(|p| p.join(name)).find(|p| p.is_file())?;
    let mut command = Command::new(binary);
    command
        .args(["--no-optional-locks", "-c", "core.fsmonitor=false", "-c"])
        .arg(if cfg!(windows) { "core.hooksPath=NUL" } else { "core.hooksPath=/dev/null" })
        .args([
            "-c",
            "core.untrackedCache=false",
            "-c",
            "gc.auto=0",
            "-C",
            folder,
            "status",
            "--porcelain=v2",
            "--branch",
            "--untracked-files=normal",
        ])
        .current_dir(folder)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null());
    for (key, _) in std::env::vars_os() {
        if key.to_string_lossy().to_ascii_uppercase().starts_with("GIT_") {
            command.env_remove(key);
        }
    }
    command.env("GIT_OPTIONAL_LOCKS", "0").env("GIT_TERMINAL_PROMPT", "0").env("LC_ALL", "C");
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        // CREATE_NO_WINDOW: no console flashes up for git.
        command.creation_flags(0x0800_0000);
    }
    let mut child = command.spawn().ok()?;
    let mut stdout = child.stdout.take()?;
    let reader = std::thread::spawn(move || {
        let mut output = Vec::new();
        let _ = stdout.by_ref().take(262_144).read_to_end(&mut output);
        output
    });
    let deadline = std::time::Instant::now() + Duration::from_secs(2);
    let status = loop {
        match child.try_wait() {
            Ok(Some(status)) => break Some(status),
            Ok(None) if std::time::Instant::now() < deadline => std::thread::sleep(Duration::from_millis(20)),
            _ => {
                let _ = child.kill();
                let _ = child.wait();
                break None;
            }
        }
    };
    let output = reader.join().ok()?;
    if !status?.success() {
        return None;
    }
    WorkspaceGitInfo::parse(&String::from_utf8_lossy(&output))
}

/// `File.Replace(source, destination, backup, ignoreMetadataErrors: true)` on Windows: `ReplaceFileW` with
/// `REPLACEFILE_IGNORE_MERGE_ERRORS`, the file it replaces kept as `backup` in one call.
#[cfg(windows)]
pub fn replace_file(source: &std::path::Path, destination: &std::path::Path, backup: &std::path::Path) -> std::io::Result<()> {
    use std::os::windows::ffi::OsStrExt;
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn ReplaceFileW(
            replaced: *const u16,
            replacement: *const u16,
            backup: *const u16,
            flags: u32,
            exclude: *mut std::ffi::c_void,
            reserved: *mut std::ffi::c_void,
        ) -> i32;
    }
    const REPLACEFILE_IGNORE_MERGE_ERRORS: u32 = 0x2;
    let wide = |path: &std::path::Path| path.as_os_str().encode_wide().chain(std::iter::once(0)).collect::<Vec<u16>>();
    let (replaced, replacement, kept) = (wide(destination), wide(source), wide(backup));
    // SAFETY: three NUL-terminated paths that live across the call; the two reserved pointers are null as documented.
    let done = unsafe {
        ReplaceFileW(
            replaced.as_ptr(),
            replacement.as_ptr(),
            kept.as_ptr(),
            REPLACEFILE_IGNORE_MERGE_ERRORS,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
        )
    };
    if done != 0 { Ok(()) } else { Err(std::io::Error::last_os_error()) }
}
