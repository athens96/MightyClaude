//! A fake of every port, for the shared crates' tests: in memory, deterministic, the same on every OS, and recording what was
//! asked of it. Each keeps its port's contract (a killed child ends, a missing secret is None, a closed surface refuses calls), so a
//! test that passes against a fake states what the real implementations must also do.

use std::collections::{BTreeMap, HashMap, VecDeque};
use std::path::{Path, PathBuf};
use std::pin::Pin;
use std::sync::{Arc, Mutex, MutexGuard};
use std::task::{Context, Poll};

use tokio::io::AsyncWrite;
use tokio::sync::{mpsc, watch};

pub use crate::files::fake::{DesktopCall, FakeFileHandles, FakePdf, FakeSaveDialog, PathProbeCall, RecordingDesktop, RecordingPathProbe};
use crate::notify::ActivationHandler;
use crate::*;

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// Every port faked, the paths under `root` ([`AppPaths::rooted`]), with the Windows profile unless `os` says otherwise.
pub fn platform(root: &Path, os: Os) -> Platform {
    Platform {
        profile: PlatformProfile::for_os(os),
        paths: Arc::new(AppPaths::rooted(root)),
        processes: Arc::new(FakeProcessHost::default()),
        ptys: Arc::new(FakePtyHost::default()),
        environment: Arc::new(FakeShellEnvironment::new(BTreeMap::from([("PATH".to_string(), "/fake/bin".to_string())]))),
        secrets: Arc::new(MemoryVault::default()),
        prefs: Arc::new(MemoryPrefs::default()),
        pairing: Arc::new(MemoryPairing::default()),
        notifier: Arc::new(RecordingNotifier::default()),
        firewall: Arc::new(FakeFirewall::default()),
        updates: Arc::new(FakeUpdateInstaller::default()),
    }
}

// ---- processes -------------------------------------------------------------------------------------------------------------

/// What the next spawned fake child does.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct FakeScript {
    pub stdout: Vec<u8>,
    pub stderr: Vec<u8>,
    pub exit_code: i32,
    /// Runs until killed (a child that never ends by itself).
    pub hang: bool,
}

/// The exit code a killed fake child reports (Windows' `TerminateJobObject(job, 1)`).
pub const KILLED: i32 = 1;

/// Children run [`FakeScript`]s in order (an empty queue runs a child that prints nothing and exits 0). Records every request and
/// what each child read on stdin.
#[derive(Default)]
pub struct FakeProcessHost {
    scripts: Mutex<VecDeque<FakeScript>>,
    spawned: Mutex<Vec<SpawnRequest>>,
    inputs: Mutex<Vec<Arc<Mutex<Vec<u8>>>>>,
    next_id: Mutex<u32>,
}

impl FakeProcessHost {
    pub fn push(&self, script: FakeScript) {
        lock(&self.scripts).push_back(script);
    }
    pub fn spawned(&self) -> Vec<SpawnRequest> {
        lock(&self.spawned).clone()
    }
    /// What the `index`th child received on stdin.
    pub fn input(&self, index: usize) -> Vec<u8> {
        lock(&self.inputs).get(index).map(|input| lock(input).clone()).unwrap_or_default()
    }
}

struct Recorder(Arc<Mutex<Vec<u8>>>);

impl AsyncWrite for Recorder {
    fn poll_write(self: Pin<&mut Self>, _: &mut Context<'_>, bytes: &[u8]) -> Poll<std::io::Result<usize>> {
        lock(&self.0).extend_from_slice(bytes);
        Poll::Ready(Ok(bytes.len()))
    }
    fn poll_flush(self: Pin<&mut Self>, _: &mut Context<'_>) -> Poll<std::io::Result<()>> {
        Poll::Ready(Ok(()))
    }
    fn poll_shutdown(self: Pin<&mut Self>, _: &mut Context<'_>) -> Poll<std::io::Result<()>> {
        Poll::Ready(Ok(()))
    }
}

/// An exit code that is set once (by the script or by a kill).
struct Exit(watch::Sender<Option<i32>>);

impl Exit {
    fn new(code: Option<i32>) -> Exit {
        Exit(watch::channel(code).0)
    }
    fn end(&self, code: i32) {
        self.0.send_if_modified(|current| {
            if current.is_some() {
                return false;
            }
            *current = Some(code);
            true
        });
    }
    async fn wait(&self) -> i32 {
        let mut receiver = self.0.subscribe();
        loop {
            if let Some(code) = *receiver.borrow_and_update() {
                return code;
            }
            if receiver.changed().await.is_err() {
                return KILLED;
            }
        }
    }
    fn ended(&self) -> bool {
        self.0.borrow().is_some()
    }
}

struct FakeChild {
    exit: Exit,
    closed: Mutex<bool>,
}

impl ChildControl for FakeChild {
    fn kill(&self) {
        self.exit.end(KILLED);
    }
    fn wait(&self) -> BoxFuture<'_, Result<i32>> {
        Box::pin(async move { Ok(self.exit.wait().await) })
    }
    fn close(&self) -> BoxFuture<'_, Result<()>> {
        Box::pin(async move {
            self.kill();
            *lock(&self.closed) = true;
            Ok(())
        })
    }
}

impl ProcessHost for FakeProcessHost {
    fn spawn(&self, request: SpawnRequest) -> BoxFuture<'_, Result<Child>> {
        Box::pin(async move {
            if !request.cwd.is_absolute() || !request.program.is_absolute() {
                return Err(PlatformError::Invalid("the program and the working folder need absolute paths".into()));
            }
            // As the Windows host checks it: a name may begin with `=` (the hidden `=C:` drive folders `env::vars` lists).
            if request.env_remove.iter().any(|name| name.is_empty() || name.chars().skip(1).any(|c| c == '=') || name.contains('\0')) {
                return Err(PlatformError::Invalid("invalid child environment".into()));
            }
            let script = lock(&self.scripts).pop_front().unwrap_or_default();
            lock(&self.spawned).push(request);
            let input = Arc::new(Mutex::new(Vec::new()));
            lock(&self.inputs).push(input.clone());
            let id = {
                let mut next = lock(&self.next_id);
                *next += 1;
                1000 + *next
            };
            let control = Arc::new(FakeChild { exit: Exit::new((!script.hang).then_some(script.exit_code)), closed: Mutex::new(false) });
            Ok(Child::new(
                id,
                Box::new(Recorder(input)),
                Box::new(std::io::Cursor::new(script.stdout)),
                Box::new(std::io::Cursor::new(script.stderr)),
                control,
            ))
        })
    }
}

// ---- pseudo terminals ------------------------------------------------------------------------------------------------------

/// Terminals that echo their input back as output (a loopback), so a test drives both directions. A test can end a terminal's
/// shell as if it had exited by itself ([`FakePtyHost::exit`]; its output stays open until the close, as a console's does) and
/// say what the shell-activity reading answers ([`FakePtyHost::set_busy`]).
#[derive(Default)]
pub struct FakePtyHost {
    opened: Mutex<Vec<PtyRequest>>,
    sizes: Arc<Mutex<Vec<PtySize>>>,
    terminals: Mutex<Vec<Arc<FakeTerminal>>>,
    /// Opens that fail, from the next one on.
    fail_opens: Mutex<usize>,
}

impl FakePtyHost {
    pub fn opened(&self) -> Vec<PtyRequest> {
        lock(&self.opened).clone()
    }
    pub fn sizes(&self) -> Vec<PtySize> {
        lock(&self.sizes).clone()
    }
    /// The `index`th terminal opened (0 the first) ends with `code`, as a shell that exited.
    pub fn exit(&self, index: usize, code: i32) {
        if let Some(terminal) = lock(&self.terminals).get(index) {
            terminal.exit.end(code);
        }
    }
    /// What the `index`th terminal's [`PtyControl::busy`] reads from now on.
    pub fn set_busy(&self, index: usize, busy: Option<bool>) {
        if let Some(terminal) = lock(&self.terminals).get(index) {
            *lock(&terminal.busy) = busy;
        }
    }
    /// Whether the `index`th terminal was closed.
    pub fn closed(&self, index: usize) -> bool {
        lock(&self.terminals).get(index).is_some_and(|terminal| lock(&terminal.input).is_none())
    }
    /// The next `count` opens fail.
    pub fn fail_next_opens(&self, count: usize) {
        *lock(&self.fail_opens) = count;
    }
}

struct FakeTerminal {
    id: u32,
    input: Mutex<Option<mpsc::Sender<Vec<u8>>>>,
    sizes: Arc<Mutex<Vec<PtySize>>>,
    exit: Exit,
    busy: Mutex<Option<bool>>,
}

impl PtyControl for FakeTerminal {
    /// Refused once the shell has ended, as C#'s `TryWrite` refuses a completed terminal.
    fn write(&self, bytes: &[u8]) -> bool {
        let fits = bytes.len() <= 65_536 || String::from_utf8_lossy(bytes).encode_utf16().count() <= 65_536;
        fits && !self.exit.ended() && lock(&self.input).as_ref().is_some_and(|sender| sender.try_send(bytes.to_vec()).is_ok())
    }
    fn busy(&self) -> Option<bool> {
        if lock(&self.input).is_none() {
            return None;
        }
        *lock(&self.busy)
    }
    fn resize(&self, size: PtySize) -> Result<()> {
        if self.exit.ended() {
            return Err(PlatformError::Invalid("the terminal has closed".into()));
        }
        lock(&self.sizes).push(size.clamped());
        Ok(())
    }
    fn process_ids(&self) -> Option<Vec<u32>> {
        (!self.exit.ended()).then(|| vec![self.id])
    }
    fn wait(&self) -> BoxFuture<'_, Result<i32>> {
        Box::pin(async move { Ok(self.exit.wait().await) })
    }
    fn close(&self) -> BoxFuture<'_, Result<()>> {
        Box::pin(async move {
            lock(&self.input).take();
            self.exit.end(KILLED);
            Ok(())
        })
    }
}

impl PtyHost for FakePtyHost {
    fn default_shell(&self) -> PathBuf {
        PathBuf::from("/fake/shell")
    }
    fn shell_arguments(&self, _shell: &Path, command: Option<&str>, _no_profile: bool) -> Vec<String> {
        command.map(|c| vec!["-c".to_string(), c.to_string()]).unwrap_or_default()
    }
    fn open(&self, request: PtyRequest) -> BoxFuture<'_, Result<PtySession>> {
        Box::pin(async move {
            if !request.cwd.is_absolute() {
                return Err(PlatformError::Invalid("a terminal needs an absolute working folder".into()));
            }
            if request.env.iter().any(|(name, value)| name.is_empty() || name.contains('=') || value.contains('\0')) {
                return Err(PlatformError::Invalid("invalid terminal environment".into()));
            }
            {
                let mut failing = lock(&self.fail_opens);
                if *failing > 0 {
                    *failing -= 1;
                    return Err(PlatformError::Io(std::io::Error::other("the fake terminal refused to start")));
                }
            }
            let id = 2000 + lock(&self.opened).len() as u32;
            lock(&self.sizes).push(request.size.clamped());
            lock(&self.opened).push(request);
            let (sender, output) = mpsc::channel(64);
            let control = Arc::new(FakeTerminal {
                id,
                input: Mutex::new(Some(sender)),
                sizes: self.sizes.clone(),
                exit: Exit::new(None),
                busy: Mutex::new(Some(false)),
            });
            lock(&self.terminals).push(control.clone());
            Ok(PtySession { id, output, control })
        })
    }
}

// ---- environment -----------------------------------------------------------------------------------------------------------

/// Resolves to the values it was given; counts resolves.
pub struct FakeShellEnvironment {
    values: BTreeMap<String, String>,
    resolves: Mutex<usize>,
}

impl FakeShellEnvironment {
    pub fn new(values: BTreeMap<String, String>) -> FakeShellEnvironment {
        FakeShellEnvironment { values, resolves: Mutex::new(0) }
    }
    pub fn resolves(&self) -> usize {
        *lock(&self.resolves)
    }
}

impl ShellEnvironment for FakeShellEnvironment {
    fn resolve(&self, _workspace: Option<&Path>, _force: bool) -> BoxFuture<'_, CliEnvironment> {
        *lock(&self.resolves) += 1;
        Box::pin(async move { self.current() })
    }
    fn current(&self) -> CliEnvironment {
        CliEnvironment { values: self.values.clone(), source: EnvironmentSource::Provided }
    }
    fn invalidate(&self, _workspace: Option<&Path>) {}
}

// ---- secrets, preferences, pairing -----------------------------------------------------------------------------------------

/// Secrets for this run only (C# `MemoryRemoteKeyVault`).
#[derive(Default)]
pub struct MemoryVault(Mutex<HashMap<String, String>>);

impl SecretVault for MemoryVault {
    fn load(&self, id: &str) -> Option<String> {
        lock(&self.0).get(id).cloned()
    }
    fn save(&self, id: &str, secret: &str) -> Result<()> {
        if secret.is_empty() {
            return Err(PlatformError::Invalid("an empty secret".into()));
        }
        lock(&self.0).insert(id.to_string(), secret.to_string());
        Ok(())
    }
    fn delete(&self, id: &str) -> Result<()> {
        lock(&self.0).remove(id);
        Ok(())
    }
}

impl std::fmt::Debug for MemoryVault {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("MemoryVault")
    }
}

#[derive(Debug, Default)]
pub struct MemoryPrefs(Mutex<HashMap<String, PrefValue>>);

impl PrefsStore for MemoryPrefs {
    fn get(&self, key: &str) -> Result<Option<PrefValue>> {
        Ok(lock(&self.0).get(key).cloned())
    }
    fn set(&self, key: &str, value: PrefValue) -> Result<()> {
        lock(&self.0).insert(key.to_string(), value);
        Ok(())
    }
    fn remove(&self, key: &str) -> Result<()> {
        lock(&self.0).remove(key);
        Ok(())
    }
}

#[derive(Debug, Default)]
pub struct MemoryPairing(Mutex<Option<PairingIdentity>>);

impl PairingStore for MemoryPairing {
    fn load(&self) -> Result<Option<PairingIdentity>> {
        Ok(lock(&self.0).clone())
    }
    fn save(&self, identity: &PairingIdentity) -> Result<()> {
        *lock(&self.0) = Some(identity.clone());
        Ok(())
    }
}

/// A clipboard in memory: its text (None: no text on it), or refusing every call while `busy` (another program holding it).
#[derive(Debug, Default)]
pub struct MemoryClipboard {
    text: Mutex<Option<String>>,
    busy: Mutex<bool>,
}

impl MemoryClipboard {
    pub fn with_text(text: &str) -> MemoryClipboard {
        MemoryClipboard { text: Mutex::new(Some(text.to_string())), busy: Mutex::new(false) }
    }
    pub fn text(&self) -> Option<String> {
        lock(&self.text).clone()
    }
    pub fn set_busy(&self, busy: bool) {
        *lock(&self.busy) = busy;
    }
}

impl Clipboard for MemoryClipboard {
    fn read_text(&self) -> Result<Option<String>> {
        if *lock(&self.busy) {
            return Err(PlatformError::Refused("the clipboard is in use".into()));
        }
        Ok(lock(&self.text).clone())
    }
    fn write_text(&self, text: &str) -> Result<()> {
        if *lock(&self.busy) {
            return Err(PlatformError::Refused("the clipboard is in use".into()));
        }
        *lock(&self.text) = Some(text.to_string());
        Ok(())
    }
}

// ---- notifications, firewall, updates --------------------------------------------------------------------------------------

#[derive(Default)]
pub struct RecordingNotifier {
    shown: Mutex<Vec<Notification>>,
    handler: Mutex<Option<ActivationHandler>>,
}

impl RecordingNotifier {
    pub fn shown(&self) -> Vec<Notification> {
        lock(&self.shown).clone()
    }
    /// The user clicks the `index`th notification.
    pub fn click(&self, index: usize) {
        let pane = lock(&self.shown).get(index).and_then(|n| n.pane_id.clone());
        let handler = lock(&self.handler).clone();
        if let Some(handler) = handler {
            handler(pane);
        }
    }
}

impl Notifier for RecordingNotifier {
    fn notify(&self, notification: Notification) -> BoxFuture<'_, Result<()>> {
        lock(&self.shown).push(notification);
        Box::pin(async { Ok(()) })
    }
    fn on_activated(&self, handler: ActivationHandler) {
        *lock(&self.handler) = Some(handler);
    }
}

/// A rule that `apply` changes; `decline` makes the next elevation answer no.
pub struct FakeFirewall {
    rule: Mutex<FirewallRule>,
    decline: Mutex<bool>,
}

impl Default for FakeFirewall {
    fn default() -> FakeFirewall {
        FakeFirewall { rule: Mutex::new(FirewallRule::Missing), decline: Mutex::new(false) }
    }
}

impl FakeFirewall {
    pub fn decline_next(&self) {
        *lock(&self.decline) = true;
    }
}

impl Firewall for FakeFirewall {
    fn read(&self) -> BoxFuture<'_, Result<FirewallRule>> {
        Box::pin(async move { Ok(lock(&self.rule).clone()) })
    }
    fn apply(&self, ports: Vec<u16>) -> BoxFuture<'_, Result<FirewallChange>> {
        Box::pin(async move {
            if ports.iter().any(|port| *port < 1024) {
                return Err(PlatformError::Invalid("firewall ports are 1024-65535".into()));
            }
            if std::mem::take(&mut *lock(&self.decline)) {
                return Ok(FirewallChange::Declined);
            }
            *lock(&self.rule) = if ports.is_empty() { FirewallRule::Missing } else { FirewallRule::Present { ports } };
            Ok(FirewallChange::Applied)
        })
    }
}

#[derive(Debug, Default)]
pub struct FakeUpdateInstaller {
    pub location: Option<PathBuf>,
    handed: Mutex<Vec<(StagedUpdate, bool)>>,
}

impl FakeUpdateInstaller {
    pub fn handed_off(&self) -> Vec<(StagedUpdate, bool)> {
        lock(&self.handed).clone()
    }
}

impl UpdateInstaller for FakeUpdateInstaller {
    fn install_location(&self) -> Option<PathBuf> {
        self.location.clone()
    }
    fn hand_off(&self, update: &StagedUpdate, relaunch: bool) -> Result<()> {
        if self.location.is_none() {
            return Err(PlatformError::Refused("this copy cannot update itself".into()));
        }
        lock(&self.handed).push((update.clone(), relaunch));
        Ok(())
    }
}

// ---- surfaces --------------------------------------------------------------------------------------------------------------

/// What happened to the fake's views, in order.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SurfaceEvent {
    Opened { pane: String, kind: SurfaceKind, bounds: DeviceRect },
    Placed { pane: String, rect: DeviceRect },
    Visible { pane: String, visible: bool },
    Focused { pane: String },
    Snapshot { pane: String },
    Navigated { pane: String, url: String },
    Closed { pane: String },
}

type Log = Arc<Mutex<Vec<SurfaceEvent>>>;

/// Views that record what is asked of them. `drawing` says which kinds are native (the Windows split by default: terminal web,
/// browser native); `fail_opens` makes the next opens fail. A browser view behaves as an engine would for its listener: each address it
/// loads (the first one, a navigation, back, forward, a reload) ends in a successful `Navigated`.
pub struct FakeSurfaces {
    native_terminal: bool,
    log: Log,
    fail_opens: Mutex<usize>,
    listeners: Mutex<std::collections::HashMap<String, BrowserListener>>,
    /// While set, every view's snapshot waits for a permit of it (as an engine's capture takes its time).
    snapshot_gate: Arc<Mutex<Option<Arc<tokio::sync::Semaphore>>>>,
}

impl FakeSurfaces {
    pub fn new(native_terminal: bool) -> FakeSurfaces {
        FakeSurfaces {
            native_terminal,
            log: Arc::default(),
            fail_opens: Mutex::new(0),
            listeners: Mutex::default(),
            snapshot_gate: Arc::default(),
        }
    }

    /// Makes every snapshot wait until the returned gate gives it a permit (`add_permits`).
    pub fn hold_snapshots(&self) -> Arc<tokio::sync::Semaphore> {
        let gate = Arc::new(tokio::sync::Semaphore::new(0));
        *lock(&self.snapshot_gate) = Some(gate.clone());
        gate
    }

    /// Sends `event` to the listener of the pane's browser view, as its engine would (a popup the page asked for, a download).
    pub fn emit(&self, pane: &str, event: BrowserEvent) {
        let listener = lock(&self.listeners).get(pane).cloned();
        if let Some(listener) = listener {
            listener.send(event);
        }
    }
    pub fn events(&self) -> Vec<SurfaceEvent> {
        lock(&self.log).clone()
    }
    pub fn clear(&self) {
        lock(&self.log).clear();
    }
    pub fn fail_next_open(&self) {
        *lock(&self.fail_opens) += 1;
    }
    fn failing(&self) -> bool {
        let mut fail = lock(&self.fail_opens);
        let failing = *fail > 0;
        *fail = fail.saturating_sub(1);
        failing
    }
    fn view(&self, pane: &str, kind: SurfaceKind, bounds: DeviceRect, url: Option<String>) -> FakeView {
        lock(&self.log).push(SurfaceEvent::Opened { pane: pane.to_string(), kind, bounds });
        FakeView {
            pane: pane.to_string(),
            log: self.log.clone(),
            state: Mutex::new((SurfaceState { bounds, visible: true, url: url.clone() }, false)),
            listener: None,
            engine: Mutex::new((url.into_iter().collect(), 0)),
            snapshot_gate: self.snapshot_gate.clone(),
        }
    }
}

impl Default for FakeSurfaces {
    fn default() -> FakeSurfaces {
        FakeSurfaces::new(false)
    }
}

pub struct FakeView {
    pane: String,
    log: Log,
    /// The view's state and whether it was closed.
    state: Mutex<(SurfaceState, bool)>,
    listener: Option<BrowserListener>,
    /// The engine's own history: its entries and the one shown.
    engine: Mutex<(Vec<String>, usize)>,
    snapshot_gate: Arc<Mutex<Option<Arc<tokio::sync::Semaphore>>>>,
}

impl FakeView {
    fn change(&self, event: SurfaceEvent, apply: impl FnOnce(&mut SurfaceState)) -> Result<()> {
        let mut state = lock(&self.state);
        if state.1 {
            return Err(PlatformError::Invalid("the view is closed".into()));
        }
        apply(&mut state.0);
        lock(&self.log).push(event);
        Ok(())
    }

    /// The engine loaded `url`: the listener hears it.
    fn loaded(&self, url: &str) {
        lock(&self.state).0.url = Some(url.to_string());
        if let Some(listener) = &self.listener {
            listener.send(BrowserEvent::Navigated { url: Some(url.to_string()), success: true });
        }
    }

    /// The engine's back (-1) or forward (+1), when it has an entry there.
    fn step(&self, by: isize, name: &str) -> Result<()> {
        let url = {
            let mut engine = lock(&self.engine);
            let Some(at) = engine.1.checked_add_signed(by).filter(|at| *at < engine.0.len()) else {
                return Ok(());
            };
            engine.1 = at;
            engine.0[at].clone()
        };
        self.change(SurfaceEvent::Navigated { pane: self.pane.clone(), url: format!("{name}:{url}") }, |_| {})?;
        self.loaded(&url);
        Ok(())
    }
}

impl NativeSurface for FakeView {
    fn place(&self, rect: DeviceRect) -> Result<()> {
        self.change(SurfaceEvent::Placed { pane: self.pane.clone(), rect }, |s| s.bounds = rect)
    }
    fn set_visible(&self, visible: bool) -> Result<()> {
        self.change(SurfaceEvent::Visible { pane: self.pane.clone(), visible }, |s| s.visible = visible)
    }
    fn focus(&self) -> Result<()> {
        self.change(SurfaceEvent::Focused { pane: self.pane.clone() }, |_| {})
    }
    fn snapshot(&self) -> BoxFuture<'_, Result<Option<String>>> {
        Box::pin(async move {
            let gate = lock(&self.snapshot_gate).clone();
            if let Some(gate) = gate {
                let _ = gate.acquire().await.map(|permit| permit.forget());
            }
            self.change(SurfaceEvent::Snapshot { pane: self.pane.clone() }, |_| {})?;
            Ok(Some("data:image/png;base64,".to_string()))
        })
    }
    fn inspect(&self) -> BoxFuture<'_, Result<SurfaceState>> {
        Box::pin(async move { Ok(lock(&self.state).0.clone()) })
    }
    fn close(&self) -> Result<()> {
        self.change(SurfaceEvent::Closed { pane: self.pane.clone() }, |s| s.visible = false)?;
        lock(&self.state).1 = true;
        Ok(())
    }
}

impl BrowserSurface for FakeView {
    fn navigate(&self, url: &str) -> Result<()> {
        self.change(SurfaceEvent::Navigated { pane: self.pane.clone(), url: url.to_string() }, |s| s.url = Some(url.to_string()))?;
        {
            let mut engine = lock(&self.engine);
            let keep = if engine.0.is_empty() { 0 } else { engine.1 + 1 };
            engine.0.truncate(keep);
            engine.0.push(url.to_string());
            engine.1 = engine.0.len() - 1;
        }
        self.loaded(url);
        Ok(())
    }
    fn go_back(&self) -> Result<()> {
        self.step(-1, "back")
    }
    fn go_forward(&self) -> Result<()> {
        self.step(1, "forward")
    }
    fn reload(&self) -> Result<()> {
        self.change(SurfaceEvent::Navigated { pane: self.pane.clone(), url: "reload".into() }, |_| {})?;
        // The state's guard goes before `loaded` takes it again.
        let url = lock(&self.state).0.url.clone();
        if let Some(url) = url {
            self.loaded(&url);
        }
        Ok(())
    }
}

impl TerminalSurface for FakeView {
    fn send_text(&self, _text: &str) -> Result<()> {
        Ok(())
    }
    fn set_font(&self, _family: &str, _size: f64) -> Result<()> {
        Ok(())
    }
    fn set_theme(&self, _light: bool) -> Result<()> {
        Ok(())
    }
}

impl SurfaceFactory for FakeSurfaces {
    fn drawing(&self, kind: SurfaceKind) -> Drawing {
        match kind {
            SurfaceKind::Terminal if !self.native_terminal => Drawing::Web,
            _ => Drawing::Native,
        }
    }
    fn open_browser(&self, options: BrowserOptions) -> BoxFuture<'_, Result<Box<dyn BrowserSurface>>> {
        Box::pin(async move {
            if self.failing() {
                return Err(PlatformError::Refused("the fake was told to fail".into()));
            }
            let mut view = self.view(&options.pane_id, SurfaceKind::Browser, options.bounds, Some(options.url.clone()));
            if let Some(listener) = options.listener {
                lock(&self.listeners).insert(options.pane_id.clone(), listener.clone());
                view.listener = Some(listener);
            }
            view.loaded(&options.url);
            Ok(Box::new(view) as Box<dyn BrowserSurface>)
        })
    }
    fn open_terminal(&self, options: TerminalOptions) -> BoxFuture<'_, Result<Box<dyn TerminalSurface>>> {
        Box::pin(async move {
            if !self.native_terminal {
                return Err(PlatformError::unsupported("SurfaceFactory", "open_terminal"));
            }
            if self.failing() {
                return Err(PlatformError::Refused("the fake was told to fail".into()));
            }
            Ok(Box::new(self.view(&options.pane_id, SurfaceKind::Terminal, options.bounds, None)) as Box<dyn TerminalSurface>)
        })
    }
    fn open_pet(&self) -> BoxFuture<'_, Result<Box<dyn PetWindow>>> {
        Box::pin(async { Err(PlatformError::unsupported("SurfaceFactory", "open_pet")) })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A fake browser's reload loads its page again and its listener hears it (it once waited on its own state's lock for ever); run
    /// on a thread of its own, so a wait fails the check instead of hanging it.
    #[test]
    fn a_fake_browser_reloads_its_page() {
        let surfaces = FakeSurfaces::new(false);
        let heard = Arc::new(Mutex::new(Vec::<String>::new()));
        let sink = heard.clone();
        let listener = BrowserListener::new(move |event| {
            if let BrowserEvent::Navigated { url: Some(url), .. } = event {
                lock(&sink).push(url);
            }
        });
        let options = BrowserOptions {
            pane_id: "p".into(),
            url: "https://example.com/".into(),
            data_dir: PathBuf::new(),
            bounds: DeviceRect::default(),
            listener: Some(listener),
        };
        let runtime = tokio::runtime::Builder::new_current_thread().build().unwrap();
        let view = runtime.block_on(surfaces.open_browser(options)).unwrap();
        let (done, finished) = std::sync::mpsc::channel();
        std::thread::spawn(move || {
            let _ = done.send(view.reload().is_ok());
        });
        assert_eq!(finished.recv_timeout(std::time::Duration::from_secs(5)), Ok(true), "the reload ends");
        assert_eq!(*lock(&heard), ["https://example.com/", "https://example.com/"], "loaded, then loaded again");
    }
}
