//! The host side of the NativeSlot protocol (docs/tauri-architecture.md §3): native views laid over the slots the page reserves.
//!
//! - `host` push: when the page calls `ready`, before the snapshot, the host says who draws what:
//!   `{kind:'host', platform:'windows'|'macos', surfaces:{terminal:'web'|'native', browser:'web'|'native'}}`. The C# host never
//!   sends it, so a page that has none keeps the C# protocol (`browserBounds`, which this host takes too).
//! - `surfaceBounds{paneId, surface, rect:{x,y,width,height} (device px), visible, occluded}`: where a slot is now. The view is shown
//!   at `rect` while `visible && !occluded` and the rectangle is not empty, else hidden. The page sends one per frame at most, and
//!   `visible:false` when the slot goes away.
//! - `surfaceAction{paneId, surface, action:'focus'|'snapshot'}`: focus answers null; snapshot answers the view's picture as a PNG
//!   `data:` URL, or null when the platform has none (the page asks just before it reports the slot occluded).
//! - Browser panes (C# MainWindow.WebShell.Panes.cs:330-438, MainWindow.Browser.cs): `browserOpen` (their state), `browserNavigate
//!   {address}` (`BrowserAddress.Resolve`), `browserBack`, `browserForward` (the engine's, with `BrowserHistory`), `browserReload`,
//!   `browserInstall` (the engine's install, only when asked). Their state comes as `browser{paneId, state}` pushes, as from the C#
//!   host: `{status: starting|ready|disabled|missing|failed, url?, canGoBack, canGoForward, installing, installMessage?,
//!   installFailed}`, pushed after every navigation the engine ends. The engine is off until the setting (`browserEngineEnabled`, read
//!   once per launch) turns it on; a pane of a workspace shares that workspace's web profile (`BrowserProfile`); popups and downloads
//!   are refused; a press in the page makes its pane the active one.
//!
//! The manager keeps one slot per (pane, kind). A view opens the first time its slot can show, hides when the slot cannot, hides on a
//! page reload until the page reports again, and closes when its pane is gone from the state.

use std::collections::{HashMap, HashSet};
use std::path::PathBuf;
use std::sync::{Arc, Mutex as StdMutex, OnceLock};

use mighty_core::browser::{self, History};
use mighty_core::locale::Locale;
use mighty_core::wire;
use mighty_platform::{
    BrowserEvent, BrowserListener, BrowserOptions, BrowserSurface, DeviceRect, Drawing, NativeSurface, Os, SurfaceFactory, SurfaceKind,
    SurfaceState, TerminalSurface,
};
use serde_json::{Map, Value, json};
use tauri::{Emitter, Manager};
use tokio::sync::Mutex;

use crate::host::{EVENT, Host, Registry, Reply, WINDOW};

/// Where host → page messages of the surfaces go.
pub type Push = Arc<dyn Fn(Value) + Send + Sync>;
/// What a press inside a browser view does with its pane (C# `ActivatePane`).
pub type Focus = Arc<dyn Fn(&str) + Send + Sync>;
/// Whether the browser engine is on this computer, and its version (C# `WebView2RuntimeLocator`).
pub type Runtime = Arc<dyn Fn() -> Option<String> + Send + Sync>;

/// A browser pane's first address before the page names one: the engine starts there and the history never lists it.
pub const BLANK: &str = "about:blank";

/// One `surfaceBounds` report.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct SlotReport {
    pub rect: DeviceRect,
    pub visible: bool,
    pub occluded: bool,
}

impl SlotReport {
    pub fn shows(&self) -> bool {
        self.visible && !self.occluded && !self.rect.is_empty()
    }

    /// `{rect:{x,y,width,height}, visible, occluded}`, whole device pixels (a missing or broken rect is empty).
    pub fn parse(args: &Value) -> SlotReport {
        SlotReport::from_rect(args.get("rect"), args)
    }

    /// C#'s `browserBounds{x, y, width, height, visible}` (the page without the `host` push): never occluded, the page hides it.
    pub fn parse_bounds(args: &Value) -> SlotReport {
        SlotReport { occluded: false, ..SlotReport::from_rect(Some(args), args) }
    }

    fn from_rect(rect: Option<&Value>, args: &Value) -> SlotReport {
        let number = |name: &str| rect.and_then(|r| r.get(name)).and_then(Value::as_f64).filter(|n| n.is_finite()).unwrap_or(0.0).round();
        let size = |name: &str| number(name).clamp(0.0, f64::from(u32::MAX)) as u32;
        let position = |name: &str| number(name).clamp(f64::from(i32::MIN), f64::from(i32::MAX)) as i32;
        SlotReport {
            rect: DeviceRect { x: position("x"), y: position("y"), width: size("width"), height: size("height") },
            visible: args.get("visible") == Some(&Value::Bool(true)),
            occluded: args.get("occluded") == Some(&Value::Bool(true)),
        }
    }
}

/// C# `BrowserAddress.Resolve` (the Mac's `BrowserAddress.resolve`): trimmed; empty is nothing; with `://` as given; else https.
pub fn resolve_address(text: &str) -> Option<String> {
    let trimmed = text.trim();
    if trimmed.is_empty() {
        return None;
    }
    let address = if trimmed.contains("://") { trimmed.to_string() } else { format!("https://{trimmed}") };
    tauri::Url::parse(&address).ok().map(String::from)
}

/// The web profile's key of a browser pane (C# `Session.WorkspaceProfileKey ?? Session.WorkspaceId`), used as a folder name: a key
/// that is no identifier (a state file can hold any text there) falls back to the workspace's id, never a path of its own.
pub fn profile_key(workspace_profile_key: Option<&str>, workspace_id: &str) -> String {
    [workspace_profile_key.unwrap_or(""), workspace_id].into_iter().find(|key| wire::identifier(key)).unwrap_or("default").to_string()
}

enum View {
    Browser(Box<dyn BrowserSurface>),
    /// The Mac's Ghostty view, opened by its pane's terminal (the Mac stage); Windows draws terminals in the page.
    #[allow(dead_code)]
    Terminal(Box<dyn TerminalSurface>),
}

impl View {
    fn native(&self) -> &dyn NativeSurface {
        match self {
            View::Browser(view) => view.as_ref(),
            View::Terminal(view) => view.as_ref(),
        }
    }
}

#[derive(Default)]
struct Slot {
    report: Option<SlotReport>,
    view: Option<View>,
    /// What the view was last told: shown, and where.
    shown: bool,
    placed: Option<DeviceRect>,
    /// A browser pane's first address, once `browserOpen` started it (or the page named one before its view opened).
    url: Option<String>,
    /// A browser pane's web profile folder.
    data_dir: Option<PathBuf>,
    /// Why the view could not open (the browser pane shows "failed").
    failed: Option<String>,
}

/// A browser pane's state as the page draws it (C# `WebBrowserPane`).
#[derive(Debug, Clone, Default)]
struct BrowserPane {
    status: &'static str,
    history: History,
    installing: bool,
    install_message: Option<String>,
    install_failed: bool,
    /// The popups its page asked for, every one refused (the smoke reads it).
    popups_refused: u32,
}

impl BrowserPane {
    /// C# `WebBrowserState`: nulls left out, as `Wire.Json` writes them.
    fn state(&self) -> Value {
        let mut state = Map::new();
        state.insert("status".into(), json!(if self.status.is_empty() { "starting" } else { self.status }));
        if let Some(url) = self.history.current() {
            state.insert("url".into(), json!(url));
        }
        state.insert("canGoBack".into(), json!(self.history.can_go_back()));
        state.insert("canGoForward".into(), json!(self.history.can_go_forward()));
        state.insert("installing".into(), json!(self.installing));
        if let Some(message) = &self.install_message {
            state.insert("installMessage".into(), json!(message));
        }
        state.insert("installFailed".into(), json!(self.install_failed));
        Value::Object(state)
    }
}

/// The browser panes' states, shared with their views' listeners (which run on the OS's UI thread and must not wait).
struct Browsers {
    panes: StdMutex<HashMap<String, BrowserPane>>,
    push: Push,
    focus: OnceLock<Focus>,
}

impl Browsers {
    fn lock(&self) -> std::sync::MutexGuard<'_, HashMap<String, BrowserPane>> {
        self.panes.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// Changes a pane's state and pushes it (C# `PushWebBrowser`).
    fn change(&self, pane: &str, edit: impl FnOnce(&mut BrowserPane)) -> Value {
        let state = {
            let mut panes = self.lock();
            let browser = panes.entry(pane.to_string()).or_default();
            edit(browser);
            browser.state()
        };
        (self.push)(json!({"kind": "browser", "paneId": pane, "state": state}));
        state
    }

    /// What a pane's view told it (`NavigationCompleted`, `NewWindowRequested`, `GotFocus`).
    fn event(&self, pane: &str, event: BrowserEvent) {
        match event {
            BrowserEvent::Navigated { url, success } => {
                // History follows the engine's own navigations, each under the address it reports, once it succeeded; the blank page a
                // view starts on is never listed.
                let visited =
                    url.filter(|_| success).and_then(|url| tauri::Url::parse(&url).ok()).map(String::from).filter(|url| url != BLANK);
                self.change(pane, |browser| {
                    if let Some(url) = &visited {
                        browser.history.visit(url);
                    }
                });
            }
            BrowserEvent::PopupRefused => self.lock().entry(pane.to_string()).or_default().popups_refused += 1,
            BrowserEvent::DownloadRefused => {}
            BrowserEvent::Focused => {
                if let Some(focus) = self.focus.get() {
                    focus(pane);
                }
            }
        }
    }
}

type Key = (String, SurfaceKind);

pub struct SurfaceManager {
    factory: Arc<dyn SurfaceFactory>,
    os: Os,
    /// The browser panes' web data: one profile per workspace under it (`BrowserProfile`).
    browser_data: PathBuf,
    push: Push,
    slots: Mutex<HashMap<Key, Slot>>,
    browsers: Arc<Browsers>,
    /// The browser setting, read once per launch (C# `browserEngineLatch`).
    engine: OnceLock<bool>,
    runtime: StdMutex<Runtime>,
}

impl SurfaceManager {
    pub fn new(factory: Arc<dyn SurfaceFactory>, os: Os, browser_data: PathBuf, push: Push) -> SurfaceManager {
        let browsers = Arc::new(Browsers { panes: StdMutex::new(HashMap::new()), push: push.clone(), focus: OnceLock::new() });
        let runtime: Runtime = Arc::new(crate::platform::browser_runtime);
        SurfaceManager {
            factory,
            os,
            browser_data,
            push,
            slots: Mutex::new(HashMap::new()),
            browsers,
            engine: OnceLock::new(),
            runtime: StdMutex::new(runtime),
        }
    }

    /// The `host` push.
    pub fn capabilities(&self) -> Value {
        let drawing = |kind| self.factory.drawing(kind).name();
        json!({
            "kind": "host",
            "platform": self.os.name(),
            "surfaces": {"terminal": drawing(SurfaceKind::Terminal), "browser": drawing(SurfaceKind::Browser)},
        })
    }

    /// Pushes the `host` message (see the module comment).
    pub fn announce(&self) {
        (self.push)(self.capabilities());
    }

    /// What a press inside a browser view does (set once, by the first `browserOpen`).
    pub fn on_focus(&self, focus: impl FnOnce() -> Focus) {
        let _ = self.browsers.focus.get_or_init(focus);
    }

    /// The browser setting, latched at its first read (`LatchBrowserEngineSetting`): a change applies after a restart.
    pub fn latch_engine(&self, enabled: bool) -> bool {
        *self.engine.get_or_init(|| enabled)
    }

    /// The smoke's browser steps: the engine counts as on (C# `ForceBrowserEngineForSmoke`), before any pane starts.
    pub fn force_engine(&self) {
        let _ = self.engine.set(true);
    }

    /// The runtime check (a test's stand-in for the OS's).
    #[cfg(test)]
    pub fn set_runtime(&self, runtime: Runtime) {
        *self.runtime.lock().unwrap_or_else(|p| p.into_inner()) = runtime;
    }

    fn runtime_version(&self) -> Option<String> {
        let runtime = self.runtime.lock().unwrap_or_else(|p| p.into_inner()).clone();
        runtime()
    }

    pub async fn bounds(&self, pane: &str, kind: SurfaceKind, report: SlotReport) -> Result<(), String> {
        if self.factory.drawing(kind) == Drawing::Web {
            return Ok(());
        }
        let mut slots = self.slots.lock().await;
        let slot = slots.entry((pane.to_string(), kind)).or_default();
        slot.report = Some(report);
        self.apply(pane, kind, slot).await
    }

    /// Opens, places, shows or hides the slot's view to match its last report.
    async fn apply(&self, pane: &str, kind: SurfaceKind, slot: &mut Slot) -> Result<(), String> {
        let Some(report) = slot.report else { return Ok(()) };
        let show = report.shows();
        if slot.view.is_none() {
            if !show || slot.failed.is_some() || kind != SurfaceKind::Browser {
                // A terminal view is opened by its pane's terminal (the Mac stage), never by a report alone.
                return Ok(());
            }
            let (Some(url), Some(data_dir)) = (slot.url.clone(), slot.data_dir.clone()) else { return Ok(()) };
            let browsers = self.browsers.clone();
            let owner = pane.to_string();
            let listener = BrowserListener::new(move |event| browsers.event(&owner, event));
            let options = BrowserOptions { pane_id: pane.to_string(), url, data_dir, bounds: report.rect, listener: Some(listener) };
            match self.factory.open_browser(options).await {
                Ok(view) => {
                    slot.view = Some(View::Browser(view));
                    slot.shown = true;
                    slot.placed = Some(report.rect);
                    self.browsers.change(pane, |browser| browser.status = "ready");
                }
                Err(error) => {
                    slot.failed = Some(error.to_string());
                    self.browsers.change(pane, |browser| browser.status = "failed");
                    return Err(error.to_string());
                }
            }
            return Ok(());
        }
        let Some(view) = &slot.view else { return Ok(()) };
        let native = view.native();
        if show {
            // Moved before it shows, so it never flashes where it was.
            if slot.placed != Some(report.rect) {
                native.place(report.rect).map_err(|e| e.to_string())?;
                slot.placed = Some(report.rect);
            }
            if !slot.shown {
                native.set_visible(true).map_err(|e| e.to_string())?;
                slot.shown = true;
            }
        } else if slot.shown {
            native.set_visible(false).map_err(|e| e.to_string())?;
            slot.shown = false;
        }
        Ok(())
    }

    /// `focus` → null; `snapshot` → a `data:` URL or null. A slot without a view answers null.
    pub async fn action(&self, pane: &str, kind: SurfaceKind, action: &str) -> Reply {
        let slots = self.slots.lock().await;
        let Some(view) = slots.get(&(pane.to_string(), kind)).and_then(|slot| slot.view.as_ref()) else { return Ok(Value::Null) };
        match action {
            "focus" => view.native().focus().map(|()| Value::Null).map_err(|e| e.to_string()),
            "snapshot" => Ok(view.native().snapshot().await.map_err(|e| e.to_string())?.map(Value::String).unwrap_or(Value::Null)),
            other => Err(format!("unknown surface action: {other}")),
        }
    }

    /// A browser pane's state now.
    fn browser_state(&self, pane: &str) -> Value {
        self.browsers.lock().get(pane).cloned().unwrap_or_default().state()
    }

    /// `browserOpen` (C# `WebBrowser` and `StartWebBrowserAsync`): the pane's state; a new pane starts: nothing while the setting is
    /// off (`disabled`), the install offer without the runtime (`missing`), else its view opens over its slot as soon as the slot can
    /// show, in the workspace's web profile.
    pub async fn browser_open(&self, pane: &str, profile_key: &str, engine_enabled: bool) -> Reply {
        if self.browsers.lock().get(pane).is_some_and(|browser| !browser.status.is_empty() && browser.status != "starting") {
            return Ok(self.browser_state(pane));
        }
        self.start(pane, profile_key, engine_enabled).await;
        Ok(self.browser_state(pane))
    }

    async fn start(&self, pane: &str, profile_key: &str, engine_enabled: bool) {
        let status = browser::engine_status(self.latch_engine(engine_enabled), self.runtime_version().as_deref());
        if status != "ready" {
            self.browsers.change(pane, |browser| browser.status = status);
            return;
        }
        self.browsers.change(pane, |browser| browser.status = "starting");
        let mut slots = self.slots.lock().await;
        let slot = slots.entry((pane.to_string(), SurfaceKind::Browser)).or_default();
        slot.data_dir = Some(browser::profile_folder(&self.browser_data, profile_key));
        if slot.url.is_none() {
            slot.url = Some(BLANK.to_string());
        }
        // An open that fails is told by the state it pushes.
        let _ = self.apply(pane, SurfaceKind::Browser, slot).await;
    }

    /// `browserNavigate{address}`: the engine goes there and the history follows its navigation; an address `BrowserAddress.Resolve`
    /// refuses does nothing. Before the view is open the address is its first.
    pub async fn browser_navigate(&self, pane: &str, address: &str) -> Reply {
        let Some(url) = resolve_address(address) else { return Ok(Value::Null) };
        let mut slots = self.slots.lock().await;
        let slot = slots.entry((pane.to_string(), SurfaceKind::Browser)).or_default();
        match &slot.view {
            Some(View::Browser(view)) => view.navigate(&url).map_err(|e| e.to_string())?,
            _ => slot.url = Some(url),
        }
        Ok(Value::Null)
    }

    /// `browserBack`, `browserForward` (the engine goes there, the history with it, the state pushed), `browserReload`.
    pub async fn browser_command(&self, pane: &str, command: &str) -> Reply {
        let slots = self.slots.lock().await;
        let Some(View::Browser(view)) = slots.get(&(pane.to_string(), SurfaceKind::Browser)).and_then(|slot| slot.view.as_ref()) else {
            return Ok(Value::Null);
        };
        match command {
            "back" | "forward" => {
                let back = command == "back";
                // The history moves first: the engine's navigation then ends on the entry it already shows (a visit of the address
                // shown changes nothing), however soon the engine reports it.
                let moved = {
                    let mut panes = self.browsers.lock();
                    let Some(browser) = panes.get_mut(pane) else { return Ok(Value::Null) };
                    if back { browser.history.go_back().is_some() } else { browser.history.go_forward().is_some() }
                };
                if !moved {
                    return Ok(Value::Null);
                }
                if let Err(error) = if back { view.go_back() } else { view.go_forward() } {
                    self.browsers.change(pane, |browser| {
                        if back {
                            browser.history.go_forward();
                        } else {
                            browser.history.go_back();
                        }
                    });
                    return Err(error.to_string());
                }
                self.browsers.change(pane, |_| {});
                Ok(Value::Null)
            }
            _ => view.reload().map(|()| Value::Null).map_err(|e| e.to_string()),
        }
    }

    /// `browserInstall` (C# `InstallWebBrowserRuntime`): only for a pane that says the runtime is missing and is not installing yet;
    /// the bootstrapper downloaded, checked and run (mighty-core `browser::install`), what it said pushed; a success starts the pane.
    pub async fn browser_install(&self, pane: &str, profile_key: &str, locale: Locale) -> Reply {
        let ready = self.browsers.lock().get(pane).is_some_and(|b| !b.installing && b.status == "missing");
        if !ready {
            return Ok(Value::Null);
        }
        self.browsers.change(pane, |browser| {
            browser.installing = true;
            browser.install_message = None;
        });
        let temporary = std::env::temp_dir().join(format!("MightyCodeWebView2Setup_{}", wire::new_id()));
        let folder = temporary.clone();
        let outcome = tauri::async_runtime::spawn_blocking(move || {
            std::fs::create_dir_all(&folder).map_err(|e| e.to_string())?;
            Ok::<_, String>(browser::install(&folder, &crate::platform::BrowserInstaller, &locale))
        })
        .await
        .map_err(|e| e.to_string())
        .and_then(|result| result);
        let _ = std::fs::remove_dir_all(&temporary);
        let (success, message) = match outcome {
            Ok(result) if result.success => (true, locale.get("browser.runtime.installSuccess")),
            Ok(result) => (false, result.error.unwrap_or_else(|| locale.get("browser.runtime.installFailed"))),
            Err(_) => (false, locale.get("browser.runtime.installFailed")),
        };
        self.browsers.change(pane, |browser| {
            browser.installing = false;
            browser.install_failed = !success;
            browser.install_message = Some(message);
        });
        if success {
            self.start(pane, profile_key, true).await;
        }
        Ok(Value::Null)
    }

    /// The popups a pane's page asked for, all refused (the smoke's check).
    pub fn popups_refused(&self, pane: &str) -> u32 {
        self.browsers.lock().get(pane).map_or(0, |browser| browser.popups_refused)
    }

    /// Closes the views of panes no longer in the state.
    pub async fn retain(&self, live: &HashSet<String>) {
        let mut slots = self.slots.lock().await;
        let gone: Vec<Key> = slots.keys().filter(|(pane, _)| !live.contains(pane)).cloned().collect();
        for key in gone {
            if let Some(view) = slots.remove(&key).and_then(|slot| slot.view) {
                let _ = view.native().close();
            }
        }
        self.browsers.lock().retain(|pane, _| live.contains(pane));
    }

    /// The page is loading again: every view hides until its slot is reported again, and a view that failed may try again.
    pub async fn page_started(&self) {
        let mut slots = self.slots.lock().await;
        for slot in slots.values_mut() {
            slot.report = None;
            slot.failed = None;
            if let Some(view) = &slot.view
                && slot.shown
            {
                let _ = view.native().set_visible(false);
                slot.shown = false;
            }
        }
    }

    /// What the OS's view really is (the smoke's check); None without a view.
    pub async fn inspect(&self, pane: &str, kind: SurfaceKind) -> Option<SurfaceState> {
        let slots = self.slots.lock().await;
        let view = slots.get(&(pane.to_string(), kind))?.view.as_ref()?;
        view.native().inspect().await.ok()
    }
}

/// The pane ids of the state now.
pub fn live_panes(host: &Host) -> HashSet<String> {
    host.view().snapshot.sessions().iter().map(|session| session.id().to_string()).collect()
}

/// Pushes straight to the page's event (the host's ordered outbox is host.rs's own). Each surface push carries its whole state, so
/// its order relative to call results does not matter; the `host` push is emitted before `ready` is handled, so it reaches the
/// page before the snapshot.
pub fn page_push(app: tauri::AppHandle) -> Push {
    Arc::new(move |message| {
        let _ = app.emit_to(WINDOW, EVENT, &message);
    })
}

fn manager(host: &Host) -> Result<Arc<SurfaceManager>, String> {
    host.app.try_state::<Arc<SurfaceManager>>().map(|state| state.inner().clone()).ok_or_else(|| "no native surfaces".to_string())
}

fn pane(args: &Value) -> Result<String, String> {
    args.get("paneId")
        .and_then(Value::as_str)
        .filter(|id| !id.is_empty())
        .map(str::to_string)
        .ok_or_else(|| "paneId is missing".to_string())
}

fn kind(args: &Value) -> Result<SurfaceKind, String> {
    args.get("surface").and_then(Value::as_str).and_then(SurfaceKind::parse).ok_or_else(|| "unknown surface".to_string())
}

/// C# `WebBrowser(id)`: a browser pane (or an agent's) of the state, its web profile's key, and the browser setting; "Session not
/// found." for any other pane.
fn browser_pane(host: &Host, pane: &str) -> Result<(String, bool), String> {
    let view = host.view();
    let session = view
        .snapshot
        .session(pane)
        .filter(|s| matches!(s.kind(), "browser" | "agentBrowser"))
        .ok_or_else(|| "Session not found.".to_string())?;
    Ok((profile_key(session.workspace_profile_key.as_deref(), session.workspace_id()), view.snapshot.browser_engine_enabled))
}

/// The page methods of the NativeSlot protocol and of the browser pane.
pub fn register(methods: &mut Registry) {
    methods.add("surfaceBounds", |host: Arc<Host>, args| async move {
        manager(&host)?.bounds(&pane(&args)?, kind(&args)?, SlotReport::parse(&args)).await.map(|()| Value::Null)
    });
    methods.add("browserBounds", |host: Arc<Host>, args| async move {
        manager(&host)?.bounds(&pane(&args)?, SurfaceKind::Browser, SlotReport::parse_bounds(&args)).await.map(|()| Value::Null)
    });
    methods.add("surfaceAction", |host: Arc<Host>, args| async move {
        let action = args.get("action").and_then(Value::as_str).unwrap_or("").to_string();
        manager(&host)?.action(&pane(&args)?, kind(&args)?, &action).await
    });
    methods.add("browserOpen", |host: Arc<Host>, args| async move {
        let id = pane(&args)?;
        let (key, enabled) = browser_pane(&host, &id)?;
        let surfaces = manager(&host)?;
        let owner = host.clone();
        // A press inside the page never reaches the page's pane slot: the browser taking the keyboard makes its pane the active one.
        surfaces.on_focus(move || {
            Arc::new(move |pane: &str| {
                let host = owner.clone();
                let pane = pane.to_string();
                tauri::async_runtime::spawn(async move {
                    let _ = crate::frame::activate_pane(&mut host.view(), &pane);
                    host.schedule();
                });
            })
        });
        surfaces.browser_open(&id, &key, enabled).await
    });
    methods.add("browserNavigate", |host: Arc<Host>, args| async move {
        let address = args.get("address").and_then(Value::as_str).unwrap_or("").to_string();
        manager(&host)?.browser_navigate(&pane(&args)?, &address).await
    });
    for (method, command) in [("browserBack", "back"), ("browserForward", "forward"), ("browserReload", "reload")] {
        methods.add(method, move |host: Arc<Host>, args| async move { manager(&host)?.browser_command(&pane(&args)?, command).await });
    }
    methods.add("browserInstall", |host: Arc<Host>, args| async move {
        let id = pane(&args)?;
        let (key, _) = browser_pane(&host, &id)?;
        manager(&host)?.browser_install(&id, &key, host.locale).await
    });
}

#[cfg(test)]
mod tests {
    use std::sync::Mutex as StdMutex;

    use mighty_platform::fake::{FakeSurfaces, SurfaceEvent};

    use super::*;

    struct Harness {
        surfaces: Arc<FakeSurfaces>,
        pushed: Arc<StdMutex<Vec<Value>>>,
        manager: SurfaceManager,
    }

    fn harness(native_terminal: bool) -> Harness {
        let surfaces = Arc::new(FakeSurfaces::new(native_terminal));
        let pushed = Arc::new(StdMutex::new(vec![]));
        let sink = pushed.clone();
        let manager = SurfaceManager::new(
            surfaces.clone(),
            Os::Windows,
            PathBuf::from("browser-data"),
            Arc::new(move |m| sink.lock().unwrap().push(m)),
        );
        manager.set_runtime(Arc::new(|| Some("130.0".into())));
        Harness { surfaces, pushed, manager }
    }

    impl Harness {
        async fn open(&self, pane: &str) -> Value {
            self.manager.browser_open(pane, "workspace", true).await.unwrap()
        }

        fn last_state(&self) -> Value {
            self.pushed.lock().unwrap().iter().rev().find(|m| m["kind"] == "browser").unwrap()["state"].clone()
        }
    }

    const RECT: DeviceRect = DeviceRect { x: 10, y: 20, width: 300, height: 200 };

    fn shown(rect: DeviceRect) -> SlotReport {
        SlotReport { rect, visible: true, occluded: false }
    }

    #[test]
    fn reports_parse_whole_device_pixels() {
        let report =
            SlotReport::parse(&json!({"rect": {"x": 10.4, "y": -3, "width": 300.6, "height": -5}, "visible": true, "occluded": false}));
        assert_eq!(report, SlotReport { rect: DeviceRect { x: 10, y: -3, width: 301, height: 0 }, visible: true, occluded: false });
        assert!(!report.shows(), "an empty rectangle never shows");
        assert_eq!(SlotReport::parse(&json!({})), SlotReport::default());
        let bounds = SlotReport::parse_bounds(&json!({"x": 1, "y": 2, "width": 3, "height": 4, "visible": true}));
        assert_eq!(bounds, SlotReport { rect: DeviceRect { x: 1, y: 2, width: 3, height: 4 }, visible: true, occluded: false });
    }

    /// `BrowserVerification.AddressResolvesLikeMacOS`.
    #[test]
    fn addresses_resolve_as_csharp_resolves_them() {
        assert_eq!(resolve_address("example.com"), Some("https://example.com/".into()));
        assert_eq!(resolve_address("  example.com "), Some("https://example.com/".into()));
        assert_eq!(resolve_address("http://example.com/path"), Some("http://example.com/path".into()));
        assert_eq!(resolve_address("https://secure.example.com"), Some("https://secure.example.com/".into()));
        assert_eq!(resolve_address("http://127.0.0.1:8080/x"), Some("http://127.0.0.1:8080/x".into()));
        assert!(resolve_address("ftp://files.example.com").is_some_and(|u| u.starts_with("ftp://")), "ftp:// kept");
        assert_eq!(resolve_address(""), None);
        assert_eq!(resolve_address("   "), None);
        assert_eq!(resolve_address("http://exa mple.com"), None, "an address that does not parse is refused, as Uri.TryCreate refuses it");
    }

    #[test]
    fn a_profile_key_is_a_folder_name_or_the_workspace() {
        assert_eq!(profile_key(Some("ws-1"), "ws-2"), "ws-1");
        assert_eq!(profile_key(None, "ws-2"), "ws-2");
        assert_eq!(profile_key(Some(r"..\..\elsewhere"), "ws-2"), "ws-2", "a key that is no identifier never names a path");
        assert_eq!(profile_key(Some(".."), ".."), "default");
    }

    #[test]
    fn the_host_push_says_who_draws_what() {
        let h = harness(false);
        assert_eq!(
            h.manager.capabilities(),
            json!({"kind": "host", "platform": "windows", "surfaces": {"terminal": "web", "browser": "native"}})
        );
        h.manager.announce();
        assert_eq!(h.pushed.lock().unwrap()[0]["kind"], "host");
    }

    #[tokio::test]
    async fn a_browser_opens_at_its_slot_then_follows_it() {
        let h = harness(false);
        // A report before browserOpen opens nothing: the view needs the pane started.
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        assert!(h.surfaces.events().is_empty());
        let state = h.open("p").await;
        assert_eq!(state["status"], "ready");
        assert_eq!(state.get("url"), None, "the blank page it starts on is no address");
        assert_eq!(h.surfaces.events(), [SurfaceEvent::Opened { pane: "p".into(), kind: SurfaceKind::Browser, bounds: RECT }]);
        h.surfaces.clear();

        // The same rectangle again does nothing; a moved one is placed.
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        let moved = DeviceRect { y: 40, ..RECT };
        h.manager.bounds("p", SurfaceKind::Browser, shown(moved)).await.unwrap();
        assert_eq!(h.surfaces.events(), [SurfaceEvent::Placed { pane: "p".into(), rect: moved }]);
        assert_eq!(h.manager.inspect("p", SurfaceKind::Browser).await.unwrap().bounds, moved);
    }

    #[tokio::test]
    async fn an_occluded_or_hidden_slot_hides_its_view_until_it_shows_again() {
        let h = harness(false);
        h.open("p").await;
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        h.surfaces.clear();
        let occluded = SlotReport { occluded: true, ..shown(RECT) };
        h.manager.bounds("p", SurfaceKind::Browser, occluded).await.unwrap();
        h.manager.bounds("p", SurfaceKind::Browser, occluded).await.unwrap();
        h.manager.bounds("p", SurfaceKind::Browser, SlotReport { visible: false, ..shown(RECT) }).await.unwrap();
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        assert_eq!(
            h.surfaces.events(),
            [SurfaceEvent::Visible { pane: "p".into(), visible: false }, SurfaceEvent::Visible { pane: "p".into(), visible: true }],
            "hidden once, shown once, never placed again at the same rectangle"
        );
        assert!(h.manager.inspect("p", SurfaceKind::Browser).await.unwrap().visible);
    }

    #[tokio::test]
    async fn a_browser_that_opens_hidden_waits_for_its_slot() {
        let h = harness(false);
        h.manager.bounds("p", SurfaceKind::Browser, SlotReport { occluded: true, ..shown(RECT) }).await.unwrap();
        assert_eq!(h.open("p").await["status"], "starting", "nothing opens under a page dialog");
        assert!(h.surfaces.events().is_empty());
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        assert!(matches!(h.surfaces.events()[0], SurfaceEvent::Opened { .. }));
        assert_eq!(h.last_state()["status"], "ready");
    }

    /// The history follows the engine's navigations, back and forward are the engine's and the history's, and every state is pushed
    /// (C# `BrowserHistory` with `NavigationCompleted`; `browserBack` / `browserForward`).
    #[tokio::test]
    async fn the_history_follows_the_engine_and_the_page_hears_of_it() {
        let h = harness(false);
        h.manager.browser_navigate("p", "example.com").await.unwrap();
        h.open("p").await;
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        assert_eq!(h.last_state()["url"], "https://example.com/", "an address named before the view opened is its first");
        h.manager.browser_navigate("p", "http://127.0.0.1:1/fixture").await.unwrap();
        assert!(h.surfaces.events().contains(&SurfaceEvent::Navigated { pane: "p".into(), url: "http://127.0.0.1:1/fixture".into() }));
        assert_eq!(
            h.last_state(),
            json!({"status": "ready", "url": "http://127.0.0.1:1/fixture", "canGoBack": true, "canGoForward": false, "installing": false, "installFailed": false})
        );
        h.manager.browser_command("p", "back").await.unwrap();
        assert_eq!((h.last_state()["url"].clone(), h.last_state()["canGoForward"].clone()), (json!("https://example.com/"), json!(true)));
        h.manager.browser_command("p", "back").await.unwrap();
        assert_eq!(h.last_state()["url"], "https://example.com/", "never back past the first page");
        h.manager.browser_command("p", "forward").await.unwrap();
        assert_eq!(h.last_state()["url"], "http://127.0.0.1:1/fixture");
        let count = h.pushed.lock().unwrap().len();
        h.manager.browser_navigate("p", "  ").await.unwrap();
        assert_eq!(h.pushed.lock().unwrap().len(), count, "an address Resolve refuses does nothing");
        // A failed navigation is no visit.
        h.surfaces.emit("p", BrowserEvent::Navigated { url: Some("https://failed.example/".into()), success: false });
        assert_eq!(h.last_state()["url"], "http://127.0.0.1:1/fixture");
    }

    #[tokio::test]
    async fn popups_are_refused_and_counted_and_focus_activates_the_pane() {
        let h = harness(false);
        let focused = Arc::new(StdMutex::new(vec![]));
        let seen = focused.clone();
        h.manager.on_focus(move || Arc::new(move |pane: &str| seen.lock().unwrap().push(pane.to_string())));
        h.open("p").await;
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        h.surfaces.emit("p", BrowserEvent::PopupRefused);
        h.surfaces.emit("p", BrowserEvent::PopupRefused);
        h.surfaces.emit("p", BrowserEvent::DownloadRefused);
        assert_eq!(h.manager.popups_refused("p"), 2);
        h.surfaces.emit("p", BrowserEvent::Focused);
        assert_eq!(*focused.lock().unwrap(), ["p"]);
    }

    /// `StartWebBrowserAsync`: nothing while the setting is off (latched for the launch), the install offer without the runtime.
    #[tokio::test]
    async fn the_setting_and_the_runtime_decide_whether_a_view_starts() {
        let h = harness(false);
        assert_eq!(h.manager.browser_open("off", "w", false).await.unwrap()["status"], "disabled");
        assert_eq!(h.manager.browser_open("later", "w", true).await.unwrap()["status"], "disabled", "read once per launch");
        let h = harness(false);
        h.manager.set_runtime(Arc::new(|| None));
        assert_eq!(h.open("p").await["status"], "missing");
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        assert!(h.surfaces.events().is_empty(), "no view without the runtime");
        let h = harness(false);
        h.manager.force_engine();
        assert_eq!(h.manager.browser_open("p", "w", false).await.unwrap()["status"], "starting", "the smoke's engine is on");
    }

    #[tokio::test]
    async fn a_view_that_cannot_open_says_failed_until_the_page_loads_again() {
        let h = harness(false);
        h.surfaces.fail_next_open();
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        assert_eq!(h.open("p").await["status"], "failed");
        assert_eq!(h.last_state()["status"], "failed");
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        assert!(h.surfaces.events().is_empty(), "no second try on every report");
        h.manager.page_started().await;
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        assert!(matches!(h.surfaces.events()[0], SurfaceEvent::Opened { .. }));
    }

    #[tokio::test]
    async fn a_reload_hides_every_view_and_a_closed_pane_closes_its_view() {
        let h = harness(false);
        h.open("p").await;
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        h.manager.page_started().await;
        assert_eq!(h.surfaces.events().last(), Some(&SurfaceEvent::Visible { pane: "p".into(), visible: false }));
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        assert_eq!(h.surfaces.events().last(), Some(&SurfaceEvent::Visible { pane: "p".into(), visible: true }));
        h.manager.retain(&HashSet::from(["other".to_string()])).await;
        assert_eq!(h.surfaces.events().last(), Some(&SurfaceEvent::Closed { pane: "p".into() }));
        assert!(h.manager.inspect("p", SurfaceKind::Browser).await.is_none());
        assert_eq!(h.manager.browser_state("p")["status"], "starting", "its state went with it");
    }

    #[tokio::test]
    async fn actions_reach_the_view_and_web_kinds_are_left_to_the_page() {
        let h = harness(false);
        assert_eq!(h.manager.action("p", SurfaceKind::Browser, "snapshot").await.unwrap(), Value::Null, "no view, no picture");
        h.open("p").await;
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        assert!(h.manager.action("p", SurfaceKind::Browser, "snapshot").await.unwrap().as_str().unwrap().starts_with("data:image/png"));
        h.manager.action("p", SurfaceKind::Browser, "focus").await.unwrap();
        assert!(h.manager.action("p", SurfaceKind::Browser, "explode").await.is_err());
        h.surfaces.clear();
        // Windows draws the terminal in the page: its reports change nothing.
        h.manager.bounds("t", SurfaceKind::Terminal, shown(RECT)).await.unwrap();
        assert!(h.surfaces.events().is_empty());
    }
}
