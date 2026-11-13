//! The host side of the NativeSlot protocol (docs/tauri-architecture.md §3): native views laid over the slots the page reserves.
//!
//! - `host` push: when the page calls `ready`, before the snapshot, the host says who draws what:
//!   `{kind:'host', platform:'windows'|'macos', surfaces:{terminal:'web'|'native', browser:'web'|'native'}}`. The C# host never
//!   sends it, so a page that has none keeps the C# protocol (`browserBounds`).
//! - `surfaceBounds{paneId, surface, rect:{x,y,width,height} (device px), visible, occluded}`: where a slot is now. The view is shown
//!   at `rect` while `visible && !occluded` and the rectangle is not empty, else hidden. The page sends one per frame at most, and
//!   `visible:false` when the slot goes away.
//! - `surfaceAction{paneId, surface, action:'focus'|'snapshot'}`: focus answers null; snapshot answers the view's picture as a PNG
//!   `data:` URL, or null when the platform has none (the page asks just before it reports the slot occluded).
//! - Browser panes also take `browserOpen` (their state), `browserNavigate{address}`, `browserBack`, `browserForward`,
//!   `browserReload`; their state changes come as `browser{paneId, state}` pushes, as from the C# host.
//!
//! The manager keeps one slot per (pane, kind). A view opens the first time its slot can show (a browser once `browserOpen` gave it
//! an address), hides when the slot cannot, hides on a page reload until the page reports again, and closes when its pane is
//! gone from the state.

use std::collections::{HashMap, HashSet};
use std::path::PathBuf;
use std::sync::Arc;

use mighty_platform::{
    BrowserOptions, BrowserSurface, DeviceRect, Drawing, NativeSurface, Os, SurfaceFactory, SurfaceKind, SurfaceState, TerminalSurface,
};
use serde_json::{Value, json};
use tauri::{Emitter, Manager};
use tokio::sync::Mutex;

use crate::host::{EVENT, Host, Registry, Reply, WINDOW};

/// Where host → page messages of the surfaces go.
pub type Push = Arc<dyn Fn(Value) + Send + Sync>;

/// A browser pane's first address (C# opens a new browser pane blank).
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
        let rect = args.get("rect");
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
    /// A browser pane's address, once `browserOpen` asked for it.
    url: Option<String>,
    /// Why the view could not open (the browser pane shows "failed").
    failed: Option<String>,
}

type Key = (String, SurfaceKind);

pub struct SurfaceManager {
    factory: Arc<dyn SurfaceFactory>,
    os: Os,
    /// The browser panes' web data (per workspace in W4).
    browser_data: PathBuf,
    push: Push,
    slots: Mutex<HashMap<Key, Slot>>,
}

impl SurfaceManager {
    pub fn new(factory: Arc<dyn SurfaceFactory>, os: Os, browser_data: PathBuf, push: Push) -> SurfaceManager {
        SurfaceManager { factory, os, browser_data, push, slots: Mutex::new(HashMap::new()) }
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
            let Some(url) = slot.url.clone() else { return Ok(()) };
            let options = BrowserOptions { pane_id: pane.to_string(), url, data_dir: self.browser_data.clone(), bounds: report.rect };
            match self.factory.open_browser(options).await {
                Ok(view) => {
                    slot.view = Some(View::Browser(view));
                    slot.shown = true;
                    slot.placed = Some(report.rect);
                }
                Err(error) => {
                    slot.failed = Some(error.to_string());
                    self.push_browser(pane, slot);
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

    fn browser_state(slot: &Slot) -> Value {
        let status = if slot.failed.is_some() { "failed" } else { "ready" };
        json!({"status": status, "url": slot.url.as_deref().unwrap_or(BLANK), "canGoBack": false, "canGoForward": false})
    }

    fn push_browser(&self, pane: &str, slot: &Slot) {
        (self.push)(json!({"kind": "browser", "paneId": pane, "state": SurfaceManager::browser_state(slot)}));
    }

    /// `browserOpen`: the pane's state; its view opens as soon as its slot can show.
    pub async fn browser_open(&self, pane: &str) -> Reply {
        let mut slots = self.slots.lock().await;
        let slot = slots.entry((pane.to_string(), SurfaceKind::Browser)).or_default();
        if slot.url.is_none() {
            slot.url = Some(BLANK.to_string());
        }
        // An open that fails is told by the state it pushes; the answer is the state either way.
        let _ = self.apply(pane, SurfaceKind::Browser, slot).await;
        Ok(SurfaceManager::browser_state(slot))
    }

    /// `browserNavigate{address}`: an address `BrowserAddress.Resolve` refuses does nothing.
    pub async fn browser_navigate(&self, pane: &str, address: &str) -> Reply {
        let Some(url) = resolve_address(address) else { return Ok(Value::Null) };
        let mut slots = self.slots.lock().await;
        let slot = slots.entry((pane.to_string(), SurfaceKind::Browser)).or_default();
        if let Some(View::Browser(view)) = &slot.view {
            view.navigate(&url).map_err(|e| e.to_string())?;
        }
        slot.url = Some(url);
        self.push_browser(pane, slot);
        Ok(Value::Null)
    }

    /// `browserBack`, `browserForward`, `browserReload`.
    pub async fn browser_command(&self, pane: &str, command: &str) -> Reply {
        let slots = self.slots.lock().await;
        let Some(View::Browser(view)) = slots.get(&(pane.to_string(), SurfaceKind::Browser)).and_then(|slot| slot.view.as_ref()) else {
            return Ok(Value::Null);
        };
        match command {
            "back" => view.go_back(),
            "forward" => view.go_forward(),
            _ => view.reload(),
        }
        .map(|()| Value::Null)
        .map_err(|e| e.to_string())
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

/// The page methods of the NativeSlot protocol and of the browser pane.
pub fn register(methods: &mut Registry) {
    methods.add("surfaceBounds", |host: Arc<Host>, args| async move {
        manager(&host)?.bounds(&pane(&args)?, kind(&args)?, SlotReport::parse(&args)).await.map(|()| Value::Null)
    });
    methods.add("surfaceAction", |host: Arc<Host>, args| async move {
        let action = args.get("action").and_then(Value::as_str).unwrap_or("").to_string();
        manager(&host)?.action(&pane(&args)?, kind(&args)?, &action).await
    });
    methods.add("browserOpen", |host: Arc<Host>, args| async move { manager(&host)?.browser_open(&pane(&args)?).await });
    methods.add("browserNavigate", |host: Arc<Host>, args| async move {
        let address = args.get("address").and_then(Value::as_str).unwrap_or("").to_string();
        manager(&host)?.browser_navigate(&pane(&args)?, &address).await
    });
    for (method, command) in [("browserBack", "back"), ("browserForward", "forward"), ("browserReload", "reload")] {
        methods.add(method, move |host: Arc<Host>, args| async move { manager(&host)?.browser_command(&pane(&args)?, command).await });
    }
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
        Harness { surfaces, pushed, manager }
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
    }

    #[test]
    fn addresses_resolve_as_csharp_resolves_them() {
        assert_eq!(resolve_address("  example.com "), Some("https://example.com/".into()));
        assert_eq!(resolve_address("http://127.0.0.1:8080/x"), Some("http://127.0.0.1:8080/x".into()));
        assert_eq!(resolve_address("   "), None);
        assert_eq!(resolve_address("http://exa mple.com"), None, "an address that does not parse is refused, as Uri.TryCreate refuses it");
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
        // A report before browserOpen opens nothing: the view needs the pane's address.
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        assert!(h.surfaces.events().is_empty());
        let state = h.manager.browser_open("p").await.unwrap();
        assert_eq!(state["status"], "ready");
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
        h.manager.browser_open("p").await.unwrap();
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
        h.manager.browser_open("p").await.unwrap();
        assert!(h.surfaces.events().is_empty(), "nothing opens under a page dialog");
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        assert!(matches!(h.surfaces.events()[0], SurfaceEvent::Opened { .. }));
    }

    #[tokio::test]
    async fn navigation_goes_to_the_view_and_the_page_hears_of_it() {
        let h = harness(false);
        h.manager.browser_navigate("p", "example.com").await.unwrap();
        assert_eq!(
            h.manager.browser_open("p").await.unwrap()["url"],
            "https://example.com/",
            "an address set before the view opens is its first"
        );
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        h.manager.browser_navigate("p", "http://127.0.0.1:1/fixture").await.unwrap();
        assert!(h.surfaces.events().contains(&SurfaceEvent::Navigated { pane: "p".into(), url: "http://127.0.0.1:1/fixture".into() }));
        let pushed = h.pushed.lock().unwrap().clone();
        assert_eq!(
            pushed.last().unwrap(),
            &json!({"kind": "browser", "paneId": "p", "state": {"status": "ready", "url": "http://127.0.0.1:1/fixture", "canGoBack": false, "canGoForward": false}})
        );
        h.manager.browser_navigate("p", "  ").await.unwrap();
        assert_eq!(h.pushed.lock().unwrap().len(), pushed.len(), "an address Resolve refuses does nothing");
    }

    #[tokio::test]
    async fn a_view_that_cannot_open_says_failed_until_the_page_loads_again() {
        let h = harness(false);
        h.surfaces.fail_next_open();
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        assert_eq!(h.manager.browser_open("p").await.unwrap()["status"], "failed");
        assert_eq!(h.pushed.lock().unwrap().last().unwrap()["state"]["status"], "failed");
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        assert!(h.surfaces.events().is_empty(), "no second try on every report");
        h.manager.page_started().await;
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        assert!(matches!(h.surfaces.events()[0], SurfaceEvent::Opened { .. }));
    }

    #[tokio::test]
    async fn a_reload_hides_every_view_and_a_closed_pane_closes_its_view() {
        let h = harness(false);
        h.manager.browser_open("p").await.unwrap();
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        h.manager.page_started().await;
        assert_eq!(h.surfaces.events().last(), Some(&SurfaceEvent::Visible { pane: "p".into(), visible: false }));
        h.manager.bounds("p", SurfaceKind::Browser, shown(RECT)).await.unwrap();
        assert_eq!(h.surfaces.events().last(), Some(&SurfaceEvent::Visible { pane: "p".into(), visible: true }));
        h.manager.retain(&HashSet::from(["other".to_string()])).await;
        assert_eq!(h.surfaces.events().last(), Some(&SurfaceEvent::Closed { pane: "p".into() }));
        assert!(h.manager.inspect("p", SurfaceKind::Browser).await.is_none());
    }

    #[tokio::test]
    async fn actions_reach_the_view_and_web_kinds_are_left_to_the_page() {
        let h = harness(false);
        assert_eq!(h.manager.action("p", SurfaceKind::Browser, "snapshot").await.unwrap(), Value::Null, "no view, no picture");
        h.manager.browser_open("p").await.unwrap();
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
