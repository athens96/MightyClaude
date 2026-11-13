//! The page's host (C# `WebBridge`, MightyCode.WinUI/WebBridge.cs, and the window state `WebShellModel` reads): page → host
//! messages arrive through one command, `host_post`; every host → page message — a call's result and every push — leaves through
//! one Tauri event, `host-message`, from one thread, so they reach the page in the order they were posted (a push posted while a
//! call is handled arrives before its result).
//!
//! - A call `{kind:"call", id, method, args?}` is answered `{kind:"result", id, ok, value?, error?}` (a null value or error left
//!   out). Calls run as C#'s `async void Receive` runs them on its UI thread: each starts in the order the page posted it and runs
//!   up to its first wait before the next starts (so what a call does at once, an `updateDraft` before a `send`, is in order), and
//!   from there each goes on its own (a stop waiting for its run, a send waiting for its process, the file picker never hold up the
//!   others). A handler that panics answers `ok:false` and the next call is handled. A method this host does not have yet answers
//!   `ok:false, error:"unimplemented: <method>"`; its name is logged once and kept for `tauri-logs/unimplemented.json`.
//! - Pushes are dropped until the page has called `ready`, which pushes the whole snapshot before its result; a page that loads
//!   again starts over. A change is sent as a `delta` (`delta.rs`, C# `WebDelta`) at most 16 ms after it, coalesced with the
//!   others of that frame (`Bridge.Flush`'s timer), and a 100 ms poll catches a change made outside a call; the frame model
//!   (`shell`) is pushed when its JSON changed, looked at after every flush and every 250 ms (its relative times move on).
//! - Every change goes through [`View::update`] (C# `DesktopService.UpdateAsync`): normalized as C# normalizes it, then queued
//!   for saving (`store::Saver`). The store writes only when the app was started with `--allow-state-write`.

use std::any::Any;
use std::collections::{BTreeSet, HashMap};
use std::future::Future;
use std::panic::AssertUnwindSafe;
use std::path::PathBuf;
use std::pin::Pin;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, OnceLock, mpsc};
use std::task::{Context, Poll};
use std::time::Duration;

use mighty_core::AppSnapshot;
use mighty_core::locale::Locale;
use mighty_core::model::PaneLayoutNode;
use mighty_core::normalize;
use mighty_core::shell::{Attention, RemoteConnection, Runtime, ShellContext, ShellModel, WorkspaceGitInfo, shell_model};
use mighty_core::store::{Saver, Store};
use mighty_graph::view::MightyPushes;
use mighty_platform::{ProcessHost, SpawnRequest};
use mighty_run::{RunEvent, RunService, ServiceEvent};
use serde_json::value::RawValue;
use serde_json::{Map, Value, json};
use tauri::{AppHandle, Emitter};

use crate::delta::Bridge;
use crate::logfile::LogFile;
use crate::panes::PaneUi;
use crate::runs::Runs;
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
    /// The run service, which owns the state once it is attached: `snapshot` is then its copy for the page (`sync`).
    service: Option<Arc<RunService>>,
    /// The never-shown pane views the page writes in (C# `views`, `WebPane`): composer, cards and Mighty state per pane.
    pub panes: HashMap<String, PaneUi>,
    /// The panes whose Mighty view the page shows, and what was last pushed for each (C# `webMighty`).
    pub mighty: MightyPushes,
}

impl View {
    /// C# `DesktopService.UpdateAsync`: the change, normalized as C# normalizes every change, then saved (queued).
    pub fn update(&mut self, change: impl FnOnce(&mut AppSnapshot)) -> Result<(), String> {
        if let Some(service) = self.service.clone() {
            // The service normalizes it under its own lock (a run's events may land meanwhile) and asks for the save.
            service
                .update(|current| {
                    let mut next = current.clone();
                    change(&mut next);
                    next
                })
                .map_err(|e| e.to_string())?;
            self.sync();
            return Ok(());
        }
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
        if let Some(service) = &self.service {
            service.replace(next.clone());
        }
        self.snapshot = next;
        self.revision += 1;
    }

    /// The page's copy taken from the service again (a run event changed it, or a change went through it).
    pub fn sync(&mut self) {
        if let Some(service) = &self.service {
            self.snapshot = service.snapshot();
            self.revision += 1;
        }
    }

    /// The state as it is now, what a save writes: the run service's once it is attached (it owns the state; the page's copy may not
    /// have taken its latest events yet), else the page's copy.
    pub fn current(&self) -> AppSnapshot {
        match &self.service {
            Some(service) => service.snapshot(),
            None => self.snapshot.clone(),
        }
    }
}

/// One message for the page: its text, and its kind for the smoke's record.
struct Outgoing {
    kind: String,
    json: String,
}

/// What a panic said, when it said it in words.
pub fn panic_text(payload: &(dyn Any + Send)) -> String {
    payload
        .downcast_ref::<&str>()
        .map(|text| text.to_string())
        .or_else(|| payload.downcast_ref::<String>().cloned())
        .unwrap_or_else(|| "a panic without a message".into())
}

/// A call's future as the dispatcher runs it: a panic in it caught into an `Err` with what it said, and `first` told once its first
/// poll returned (the call ran up to its first wait, or to its end, or panicked).
pub struct Call<F: Future> {
    future: Pin<Box<F>>,
    first: Option<tokio::sync::oneshot::Sender<()>>,
}

impl<F: Future> Call<F> {
    pub fn new(future: F, first: tokio::sync::oneshot::Sender<()>) -> Call<F> {
        Call { future: Box::pin(future), first: Some(first) }
    }
}

impl<F: Future> Future for Call<F> {
    type Output = Result<F::Output, String>;

    fn poll(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Self::Output> {
        let this = self.get_mut();
        let polled = std::panic::catch_unwind(AssertUnwindSafe(|| this.future.as_mut().poll(cx)));
        if let Some(first) = this.first.take() {
            let _ = first.send(());
        }
        match polled {
            Ok(Poll::Pending) => Poll::Pending,
            Ok(Poll::Ready(value)) => Poll::Ready(Ok(value)),
            Err(panic) => Poll::Ready(Err(panic_text(panic.as_ref()))),
        }
    }
}

/// One pass of a clock's loop: a panic in it is logged (once for each clock and message) and the loop goes on with its next pass.
fn guarded_pass(log: &LogFile, logged: &Mutex<BTreeSet<String>>, clock: &str, work: impl FnOnce()) {
    if let Err(panic) = std::panic::catch_unwind(AssertUnwindSafe(work)) {
        let line = format!("{clock}: a pass panicked: {}", panic_text(panic.as_ref()));
        if lock(logged).insert(line.clone()) {
            log.line(&line);
        }
    }
}

pub struct Host {
    pub app: AppHandle,
    pub data_dir: PathBuf,
    /// Every child the host starts (`Platform::processes`; README: Child processes).
    pub processes: Arc<dyn ProcessHost>,
    pub locale: Locale,
    pub version: String,
    pub log: LogFile,
    pub smoke: Option<Smoke>,
    /// The run engine, attached once the host exists (runs.rs).
    runs: OnceLock<Arc<Runs>>,
    view: Arc<Mutex<View>>,
    saver: Arc<Saver>,
    /// The profile's store: what it approved is what a save may write (`approve_local`).
    store: Arc<Mutex<Store>>,
    registry: Registry,
    outbox: Mutex<mpsc::Sender<Outgoing>>,
    unimplemented: Mutex<BTreeSet<String>>,
    /// The clocks' panics already logged.
    panics: Mutex<BTreeSet<String>>,
    /// A flush is waiting on its 16 ms timer.
    flush_pending: AtomicBool,
    closed: AtomicBool,
}

pub fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

impl Host {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        app: AppHandle,
        data_dir: PathBuf,
        processes: Arc<dyn ProcessHost>,
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
            service: None,
            panes: HashMap::new(),
            mighty: MightyPushes::default(),
        }));
        // Saves: the state as it is now when a scheduled one fires; written by the store (which writes nothing unless allowed).
        let (current, writer, failed_view, failed_log) = (view.clone(), store.clone(), view.clone(), LogFile::new(log.dir().to_path_buf()));
        let failed_locale = locale;
        let saver = Arc::new(Saver::new(
            move || lock(&current).current(),
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
            processes,
            locale,
            version,
            log,
            smoke,
            runs: OnceLock::new(),
            view,
            saver,
            store,
            registry,
            outbox: Mutex::new(outbox),
            unimplemented: Mutex::new(BTreeSet::new()),
            panics: Mutex::new(BTreeSet::new()),
            flush_pending: AtomicBool::new(false),
            closed: AtomicBool::new(false),
        })
    }

    pub fn view(&self) -> MutexGuard<'_, View> {
        lock(&self.view)
    }

    /// C# `StateStore.ApproveLocal`: a folder the user picked, as a workspace a save may write (the one listed for it, if any).
    pub fn approve_local(&self, path: &std::path::Path) -> std::io::Result<mighty_core::model::Workspace> {
        lock(&self.store).approve_local(path)
    }

    /// The run service takes the state from here on (runs.rs).
    pub fn attach(&self, runs: Arc<Runs>) {
        let mut view = self.view();
        view.service = Some(runs.service.clone());
        let _ = self.runs.set(runs);
    }

    /// The run engine, or the refusal a page method gives without it (only while the app starts).
    pub fn engine(&self) -> Result<Arc<Runs>, String> {
        self.runs.get().cloned().ok_or_else(|| "the run engine is not ready".to_string())
    }

    /// The run engine, when attached.
    pub fn runs(&self) -> Option<&Arc<Runs>> {
        self.runs.get()
    }

    /// Every pane view's composer and cards are looked at again on the next tick (a runtime read, a setting).
    pub fn panes_changed(&self) {
        for pane in self.view().panes.values_mut() {
            pane.dirty = true;
        }
    }

    /// One event of the run service (C# `RunEventReceived`, `ToolPermissionChanged`, the save queue).
    pub fn service_event(self: &Arc<Self>, event: ServiceEvent) {
        match event {
            ServiceEvent::Run { event, .. } => {
                let drain = {
                    let mut view = self.view();
                    view.sync();
                    // WebBridge.ReceiveRunEvent: a log line and a graph run go without their record, which the delta carries a frame later.
                    let light = if matches!(event.kind.as_str(), "log" | "graph_run") {
                        RunEvent { entry: None, graph_run: None, ..(*event).clone() }
                    } else {
                        (*event).clone()
                    };
                    self.push(&view, "event", format!("{{\"kind\":\"event\",\"event\":{}}}", light.to_json()));
                    // The window drops the pane's waiting requests once its run ended (MainWindow.cs:88 ClearToolPermissions).
                    if event.kind == "status"
                        && matches!(event.status.as_deref(), Some("stopped" | "completed" | "error"))
                        && let Some(runs) = self.runs.get()
                    {
                        runs.service.clear_permissions(&event.session_id);
                    }
                    crate::composer::run_event(&mut view, &event)
                };
                self.schedule();
                if drain {
                    crate::composer::drain_queue(self.clone(), event.session_id.clone());
                }
            }
            ServiceEvent::ToolPermission(request) => {
                let mut view = self.view();
                self.push(&view, "permission", format!("{{\"kind\":\"permission\",\"request\":{}}}", request.to_json()));
                if let Some(pane) = view.panes.get_mut(&request.run_id) {
                    pane.dirty = true;
                }
                view.mighty.dirty(&request.run_id);
            }
            // The service's own state: the page's copy may not have taken the event this save is for yet (a run's end is saved
            // before its status event comes through).
            ServiceEvent::SaveNow => self.saver.queue(self.current()),
            ServiceEvent::SaveSoon => self.saver.schedule(),
            // The resume list's known sessions are W4's; a request starting needs nothing more here.
            ServiceEvent::RequestStarting(_) | ServiceEvent::RememberSession(_) => {}
        }
    }

    /// Posts a message whatever the page's state (a call's result).
    fn post(&self, kind: &str, json: String) {
        let _ = lock(&self.outbox).send(Outgoing { kind: kind.to_string(), json });
    }

    /// Posts a push, or drops it when the page has not called `ready` since it loaded.
    pub fn push(&self, view: &View, kind: &str, json: String) {
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
        // The page asks for its panes' composer, cards and Mighty views again as it mounts them.
        view.mighty = MightyPushes::default();
        for pane in view.panes.values_mut() {
            pane.forget_sent();
        }
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
            host.pass("flush", || host.flush());
        });
    }

    /// One pass of a clock: a panic in it is logged and the clock goes on.
    fn pass(&self, clock: &str, work: impl FnOnce()) {
        guarded_pass(&self.log, &self.panics, clock, work);
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
                    host.pass("bridge-poll", || {
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
                    });
                }
            })
            .expect("the bridge poll");
        let host = self.clone();
        std::thread::Builder::new()
            .name("pane-clock".into())
            .spawn(move || {
                // The composer and cards pushes (30 ms coalesce, C# FlushWebComposers / FlushWebCards) and the Mighty views' (120 ms).
                let mut ticks = 0u64;
                while !host.closed.load(Ordering::SeqCst) {
                    std::thread::sleep(Duration::from_millis(30));
                    ticks += 1;
                    host.pass("pane-clock", || crate::panes::flush(&host));
                    if ticks.is_multiple_of(4) {
                        host.pass("mighty-clock", || crate::mighty::flush(&host));
                    }
                }
            })
            .expect("the pane clock");
        let host = self.clone();
        std::thread::Builder::new()
            .name("shell-clock".into())
            .spawn(move || {
                while !host.closed.load(Ordering::SeqCst) {
                    std::thread::sleep(Duration::from_millis(250));
                    host.pass("shell-clock", || {
                        let mut view = host.view();
                        host.push_shell(&mut view);
                    });
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
        tauri::async_runtime::spawn(async move {
            let info = read_git(host.processes.as_ref(), &folder).await;
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
        // Every run stopped first (its children with it), as DisposeAsync stops them before its last save.
        if let Some(runs) = self.runs.get() {
            tauri::async_runtime::block_on(runs.service.dispose());
        }
        // The service's state after every run ended, not the page's copy, which the runs' last events may not have reached.
        self.saver.close(self.current());
    }

    /// The state as it is now, for a save ([`View::current`]).
    pub fn current(&self) -> AppSnapshot {
        self.view().current()
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

    /// One page → host message (C#'s `async void Receive`, see the module comment): `first` is told once the handler first returned
    /// (it ran up to its first wait, or to its end), and the dispatcher starts the next call only then. A handler's panic answers
    /// `ok:false` with what it said, and is logged with the method's name.
    pub async fn dispatch(self: Arc<Self>, message: Value, first: tokio::sync::oneshot::Sender<()>) {
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
            Some(handler) => match Call::new(handler(self.clone(), args), first).await {
                Ok(reply) => reply,
                Err(panic) => {
                    self.log.line(&format!("{method} panicked: {panic}"));
                    Err(panic)
                }
            },
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

/// C# `WorkspaceGitInfo.ReadAsync` (WorkspaceGitInfo.cs:32-73): `git status --porcelain=v2 --branch` in the folder through the
/// platform's `ProcessHost`, locks, hooks and fsmonitor off, every `GIT_*` variable of the app taken out of git's environment and
/// three set (`Command`), stdin closed, at most 2 s and the first 256 KB of its output; None when git is not on PATH, the folder is
/// gone, git cannot start (the Mac's `ProcessHost` is still `Unsupported`), runs too long or fails.
pub async fn read_git(processes: &dyn ProcessHost, folder: &str) -> Option<WorkspaceGitInfo> {
    use tokio::io::AsyncReadExt;
    if !normalize::fully_qualified(folder) || !std::path::Path::new(folder).is_dir() {
        return None;
    }
    let name = if cfg!(windows) { "git.exe" } else { "git" };
    let path = std::env::var_os("PATH")?;
    let binary = std::env::split_paths(&path).map(|p| p.join(name)).find(|p| p.is_file())?;
    let hooks = if cfg!(windows) { "core.hooksPath=NUL" } else { "core.hooksPath=/dev/null" };
    let arguments = [
        "--no-optional-locks",
        "-c",
        "core.fsmonitor=false",
        "-c",
        hooks,
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
    ];
    let mut request = SpawnRequest::new(binary, arguments, folder);
    for (key, _) in std::env::vars_os() {
        let key = key.to_string_lossy().into_owned();
        if key.to_ascii_uppercase().starts_with("GIT_") {
            request = request.without(key);
        }
    }
    let request = request.env("GIT_OPTIONAL_LOCKS", "0").env("GIT_TERMINAL_PROMPT", "0").env("LC_ALL", "C");
    let mut child = processes.spawn(request).await.ok()?;
    drop(child.stdin.take());
    let control = child.control();
    let (stdout, stderr) = (child.stdout.take(), child.stderr.take());
    // ReadBounded: the first 256 KB kept, the rest drained so git never blocks on a full pipe.
    async fn bounded(reader: Option<mighty_platform::process::ChildReader>, keep: usize) -> Vec<u8> {
        let mut kept = Vec::new();
        let Some(mut reader) = reader else { return kept };
        let mut buffer = [0u8; 4096];
        while let Ok(read) = reader.read(&mut buffer).await {
            if read == 0 {
                break;
            }
            let room = keep.saturating_sub(kept.len());
            kept.extend_from_slice(&buffer[..read.min(room)]);
        }
        kept
    }
    let waiting = control.clone();
    // Both pipes are read while git runs, as C# starts its readers before it waits.
    let outcome = tokio::time::timeout(Duration::from_secs(2), async move {
        let (exit, output, _) = tokio::join!(waiting.wait(), bounded(stdout, 262_144), bounded(stderr, 0));
        (exit, output)
    })
    .await;
    // `finally { process.Kill(); }`, then `await using` disposes it.
    control.kill();
    let _ = control.close().await;
    let (exit, output) = outcome.ok()?;
    if exit.ok()? != 0 {
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

#[cfg(test)]
mod tests {
    use super::*;

    fn failing_handler() -> Reply {
        panic!("a handler failed")
    }

    /// Runs `calls` as the dispatcher does: each its own task, the next started once the one before first returned.
    async fn dispatch_all(calls: Vec<Pending>) -> Vec<tokio::task::JoinHandle<Result<Reply, String>>> {
        let mut running = Vec::new();
        for call in calls {
            let (first, returned) = tokio::sync::oneshot::channel();
            running.push(tokio::spawn(Call::new(call, first)));
            let _ = returned.await;
        }
        running
    }

    /// C#'s `async void Receive`: every call's synchronous part runs in the order the page posted them, a call waiting (a stop, a
    /// start, the file picker) holds up no other, and a handler's panic is an answer, not the dispatcher's end.
    #[tokio::test]
    async fn calls_start_in_order_wait_apart_and_survive_a_panic() {
        let seen: Arc<Mutex<Vec<&'static str>>> = Arc::default();
        let (release, gate) = tokio::sync::oneshot::channel::<()>();
        let (waiting, quick, after) = (seen.clone(), seen.clone(), seen.clone());
        let calls: Vec<Pending> = vec![
            Box::pin(async move {
                lock(&waiting).push("stop asked");
                let _ = gate.await;
                lock(&waiting).push("stop done");
                Ok(json!("stopped"))
            }),
            Box::pin(async move {
                lock(&quick).push("draft");
                Ok(Value::Null)
            }),
            Box::pin(async move { failing_handler() }),
            Box::pin(async move {
                lock(&after).push("send");
                Ok(Value::Null)
            }),
        ];
        let mut running = dispatch_all(calls).await.into_iter();
        let (stop, draft, panicked, send) =
            (running.next().unwrap(), running.next().unwrap(), running.next().unwrap(), running.next().unwrap());
        assert_eq!(draft.await.unwrap(), Ok(Ok(Value::Null)), "the call behind the waiting one is answered");
        assert_eq!(panicked.await.unwrap(), Err("a handler failed".to_string()), "the panic is the call's answer");
        assert_eq!(send.await.unwrap(), Ok(Ok(Value::Null)), "the calls after the panic still run");
        assert_eq!(*lock(&seen), ["stop asked", "draft", "send"], "each began in the order posted, none waited for the stop");
        release.send(()).unwrap();
        assert_eq!(stop.await.unwrap(), Ok(Ok(json!("stopped"))));
        assert_eq!(*lock(&seen), ["stop asked", "draft", "send", "stop done"]);
    }

    /// A clock's pass that panics is logged once and the clock goes on.
    #[test]
    fn a_panicking_pass_is_logged_once_and_the_clock_goes_on() {
        let directory = std::env::temp_dir().join(format!("mighty-host-pass-{}", std::process::id()));
        let log = LogFile::new(directory.clone());
        let logged = Mutex::new(BTreeSet::new());
        let mut passes = 0;
        for _ in 0..3 {
            guarded_pass(&log, &logged, "shell-clock", || panic!("the flush failed"));
            guarded_pass(&log, &logged, "shell-clock", || passes += 1);
        }
        assert_eq!(passes, 3, "every pass after a panic still ran");
        let text = std::fs::read_to_string(directory.join("host.log")).unwrap();
        assert_eq!(text.matches("shell-clock: a pass panicked: the flush failed").count(), 1, "{text}");
        let _ = std::fs::remove_dir_all(&directory);
    }

    /// A save writes the run service's state, which owns it: the page's copy may lag behind the event the save is for.
    #[test]
    fn a_save_writes_the_service_state_not_the_page_copy() {
        let environment = Arc::new(mighty_platform::fake::FakeShellEnvironment::new(Default::default()));
        let catalog = Arc::new(mighty_run::providers::ProviderCatalog::new(
            Arc::new(mighty_platform::fake::FakeProcessHost::default()),
            environment,
            None,
        ));
        let options = mighty_run::service::RunServiceOptions {
            plugin_directory: String::new(),
            permission_card: false,
            remote: None,
            configure: None,
            admitting: None,
        };
        let (service, _events) = RunService::new(AppSnapshot::default(), catalog, options);
        let service = Arc::new(service);
        let mut view = View {
            snapshot: AppSnapshot::default(),
            revision: 0,
            ready: false,
            bridge: Bridge::default(),
            sent_shell: None,
            layout_defaults: HashMap::new(),
            shows_dashboard: false,
            dialog_open: false,
            error: None,
            git: None,
            saver: None,
            service: None,
            panes: HashMap::new(),
            mighty: MightyPushes::default(),
        };
        assert_eq!(view.current().theme(), AppSnapshot::default().theme(), "without the service, the page's copy");
        view.service = Some(service.clone());
        service.update(|state| AppSnapshot { theme: Some("light".into()), ..state.clone() }).unwrap();
        assert_ne!(view.snapshot.theme(), "light", "the page's copy has not taken the change yet");
        assert_eq!(view.current().theme(), "light", "the save takes the service's state");
    }
}
