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

/// Terminals that echo their input back as output (a loopback), so a test drives both directions.
#[derive(Default)]
pub struct FakePtyHost {
    opened: Mutex<Vec<PtyRequest>>,
    sizes: Arc<Mutex<Vec<PtySize>>>,
}

impl FakePtyHost {
    pub fn opened(&self) -> Vec<PtyRequest> {
        lock(&self.opened).clone()
    }
    pub fn sizes(&self) -> Vec<PtySize> {
        lock(&self.sizes).clone()
    }
}

struct FakeTerminal {
    id: u32,
    input: Mutex<Option<mpsc::Sender<Vec<u8>>>>,
    sizes: Arc<Mutex<Vec<PtySize>>>,
    exit: Exit,
}

impl PtyControl for FakeTerminal {
    fn write(&self, bytes: &[u8]) -> bool {
        let fits = bytes.len() <= 65_536 || String::from_utf8_lossy(bytes).encode_utf16().count() <= 65_536;
        fits && lock(&self.input).as_ref().is_some_and(|sender| sender.try_send(bytes.to_vec()).is_ok())
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
            let id = 2000 + lock(&self.opened).len() as u32;
            lock(&self.sizes).push(request.size.clamped());
            lock(&self.opened).push(request);
            let (sender, output) = mpsc::channel(64);
            let control = Arc::new(FakeTerminal { id, input: Mutex::new(Some(sender)), sizes: self.sizes.clone(), exit: Exit::new(None) });
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
/// browser native); `fail_opens` makes the next opens fail.
pub struct FakeSurfaces {
    native_terminal: bool,
    log: Log,
    fail_opens: Mutex<usize>,
}

impl FakeSurfaces {
    pub fn new(native_terminal: bool) -> FakeSurfaces {
        FakeSurfaces { native_terminal, log: Arc::default(), fail_opens: Mutex::new(0) }
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
        FakeView { pane: pane.to_string(), log: self.log.clone(), state: Mutex::new((SurfaceState { bounds, visible: true, url }, false)) }
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
        self.change(SurfaceEvent::Navigated { pane: self.pane.clone(), url: url.to_string() }, |s| s.url = Some(url.to_string()))
    }
    fn go_back(&self) -> Result<()> {
        Err(PlatformError::unsupported("BrowserSurface", "go_back"))
    }
    fn go_forward(&self) -> Result<()> {
        Err(PlatformError::unsupported("BrowserSurface", "go_forward"))
    }
    fn reload(&self) -> Result<()> {
        self.change(SurfaceEvent::Navigated { pane: self.pane.clone(), url: "reload".into() }, |_| {})
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
            Ok(Box::new(self.view(&options.pane_id, SurfaceKind::Browser, options.bounds, Some(options.url))) as Box<dyn BrowserSurface>)
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
