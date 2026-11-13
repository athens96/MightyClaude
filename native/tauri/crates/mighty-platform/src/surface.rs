//! Native views the host lays over the page (docs/tauri-architecture.md §3, NativeSlot). The page reserves an empty slot and
//! reports where it is (`surfaceBounds`); the host's `SurfaceManager` asks the platform for a view of the slot's kind and moves,
//! shows and hides it there. Which kinds an OS draws natively is its [`SurfaceFactory::drawing`]:
//!
//! | kind | Windows | Mac |
//! |---|---|---|
//! | terminal | `web` (xterm.js in the page, ConPTY) | `native` (Ghostty's NSView, libghostty) |
//! | browser | `native` (a WebView2 child webview) | `native` (CEF or WKWebView, chosen by `browser.engineEnabled`) |

use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::Arc;

use crate::{BoxFuture, Result};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum SurfaceKind {
    Terminal,
    Browser,
}

impl SurfaceKind {
    pub const ALL: [SurfaceKind; 2] = [SurfaceKind::Terminal, SurfaceKind::Browser];

    /// The name the protocol uses (`surface` of `surfaceBounds`, the keys of the `host` push's `surfaces`).
    pub fn name(self) -> &'static str {
        match self {
            SurfaceKind::Terminal => "terminal",
            SurfaceKind::Browser => "browser",
        }
    }

    pub fn parse(name: &str) -> Option<SurfaceKind> {
        SurfaceKind::ALL.into_iter().find(|kind| kind.name() == name)
    }
}

/// Who draws a kind of pane.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Drawing {
    /// The page draws it (xterm.js); the host only feeds it.
    Web,
    /// The platform lays a native view over the page's slot.
    Native,
}

impl Drawing {
    pub fn name(self) -> &'static str {
        match self {
            Drawing::Web => "web",
            Drawing::Native => "native",
        }
    }
}

/// A rectangle of the page in device pixels (CSS pixels × devicePixelRatio, rounded outwards to whole pixels as the page's
/// `slotBounds` does), from the page's top left, which is the window's client area's top left.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct DeviceRect {
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
}

impl DeviceRect {
    pub fn is_empty(&self) -> bool {
        self.width == 0 || self.height == 0
    }

    /// The same rectangle in logical units (points) at `scale` device pixels per point: what an NSView frame takes. (Tauri's
    /// webviews take physical pixels, so Windows passes the device rectangle through unchanged.)
    pub fn to_logical(&self, scale: f64) -> LogicalRect {
        let scale = if scale > 0.0 { scale } else { 1.0 };
        LogicalRect {
            x: f64::from(self.x) / scale,
            y: f64::from(self.y) / scale,
            width: f64::from(self.width) / scale,
            height: f64::from(self.height) / scale,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct LogicalRect {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

/// What the OS's view really is, read back from the OS (not what was last asked): the smoke compares it with the page's slot.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct SurfaceState {
    pub bounds: DeviceRect,
    pub visible: bool,
    /// A browser's current address.
    pub url: Option<String>,
}

/// One native view over the page.
///
/// Contract: every call returns without waiting for the UI thread to draw (the view follows the page's frame); `place` with an
/// unchanged rectangle and `set_visible` with the current state do nothing visible; after `close` the view is gone and every call
/// is an error.
pub trait NativeSurface: Send + Sync {
    fn place(&self, rect: DeviceRect) -> Result<()>;
    fn set_visible(&self, visible: bool) -> Result<()>;
    /// Gives the view the keyboard (the page asked: a click in the slot, `surfaceAction{action:'focus'}`).
    fn focus(&self) -> Result<()>;
    /// The view's picture now, as a PNG `data:` URL, taken just before it hides under a page overlay so the slot can show it
    /// meanwhile; None when this platform has no picture to give.
    fn snapshot(&self) -> BoxFuture<'_, Result<Option<String>>>;
    fn inspect(&self) -> BoxFuture<'_, Result<SurfaceState>>;
    fn close(&self) -> Result<()>;
}

/// What a browser view tells its host as it browses (the engine's events C#'s pane follows, MainWindow.WebShell.Panes.cs:385-395).
/// Sent on the OS's UI thread: a listener must not block.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum BrowserEvent {
    /// A navigation ended: the address the engine reports (else the one it started with) and whether it succeeded. The host's
    /// history follows these (C# `BrowserHistory.Visit` on `NavigationCompleted`).
    Navigated { url: Option<String>, success: bool },
    /// The page asked for a new window (`window.open`, a link to one): refused, nothing opened.
    PopupRefused,
    /// A download started: cancelled.
    DownloadRefused,
    /// The view took the keyboard (a press in the page): its pane becomes the active one.
    Focused,
}

/// Where a browser view sends its [`BrowserEvent`]s.
#[derive(Clone)]
pub struct BrowserListener(Arc<dyn Fn(BrowserEvent) + Send + Sync>);

impl BrowserListener {
    pub fn new(listener: impl Fn(BrowserEvent) + Send + Sync + 'static) -> BrowserListener {
        BrowserListener(Arc::new(listener))
    }

    pub fn send(&self, event: BrowserEvent) {
        (self.0)(event);
    }
}

impl std::fmt::Debug for BrowserListener {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("BrowserListener")
    }
}

impl PartialEq for BrowserListener {
    fn eq(&self, other: &BrowserListener) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
}

impl Eq for BrowserListener {}

/// A browser pane's view: it refuses popups and downloads, shows no script dialog, and tells its listener where it went.
pub trait BrowserSurface: NativeSurface {
    /// Loads `url` (already resolved: C# `BrowserAddress.Resolve`).
    fn navigate(&self, url: &str) -> Result<()>;
    /// The engine's own back and forward (C# `CoreWebView2.GoBack`/`GoForward`); the host's history moves with them.
    fn go_back(&self) -> Result<()>;
    fn go_forward(&self) -> Result<()>;
    fn reload(&self) -> Result<()>;
}

/// A terminal the platform draws itself (the Mac's Ghostty view, which owns its PTY). Output never crosses the bridge; title,
/// working folder and the clipboard come back through the host's `terminal` pushes as for the web terminal.
pub trait TerminalSurface: NativeSurface {
    /// Text as if typed or pasted (the composer's "run in terminal", a paste the page forwards).
    fn send_text(&self, text: &str) -> Result<()>;
    fn set_font(&self, family: &str, size: f64) -> Result<()>;
    fn set_theme(&self, light: bool) -> Result<()>;
}

/// The companion pet: a window of its own over every app, never activated, clicks passing through where it draws nothing
/// (Windows: a layered tool HWND; the Mac: a non-activating NSPanel).
pub trait PetWindow: Send + Sync {
    /// Shows the window at `frame` in screen points.
    fn show_at(&self, frame: LogicalRect) -> Result<()>;
    fn hide(&self) -> Result<()>;
    fn set_click_through(&self, on: bool) -> Result<()>;
    fn close(&self) -> Result<()>;
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BrowserOptions {
    pub pane_id: String,
    /// The first address.
    pub url: String,
    /// Where its web data lives (cookies, storage): its workspace's profile (C# `BrowserProfile`), shared by that workspace's panes.
    pub data_dir: PathBuf,
    /// The slot it opens over (it opens shown).
    pub bounds: DeviceRect,
    /// Where its events go, from before its first address loads.
    pub listener: Option<BrowserListener>,
}

#[derive(Clone, PartialEq, Eq)]
pub struct TerminalOptions {
    pub pane_id: String,
    pub cwd: PathBuf,
    pub program: Option<PathBuf>,
    pub args: Vec<String>,
    /// The whole environment (the resolved CLI environment).
    pub env: BTreeMap<String, String>,
    pub bounds: DeviceRect,
}

impl std::fmt::Debug for TerminalOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("TerminalOptions")
            .field("pane_id", &self.pane_id)
            .field("cwd", &self.cwd)
            .field("bounds", &self.bounds)
            .finish_non_exhaustive()
    }
}

/// Makes the native views of one window, and says which kinds the page must draw itself.
pub trait SurfaceFactory: Send + Sync {
    fn drawing(&self, kind: SurfaceKind) -> Drawing;
    fn open_browser(&self, options: BrowserOptions) -> BoxFuture<'_, Result<Box<dyn BrowserSurface>>>;
    fn open_terminal(&self, options: TerminalOptions) -> BoxFuture<'_, Result<Box<dyn TerminalSurface>>>;
    fn open_pet(&self) -> BoxFuture<'_, Result<Box<dyn PetWindow>>>;
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn kinds_have_the_protocol_names() {
        assert_eq!(SurfaceKind::parse("browser"), Some(SurfaceKind::Browser));
        assert_eq!(SurfaceKind::parse("terminal"), Some(SurfaceKind::Terminal));
        assert_eq!(SurfaceKind::parse("pet"), None);
        assert_eq!((Drawing::Web.name(), Drawing::Native.name()), ("web", "native"));
    }

    #[test]
    fn device_pixels_become_points() {
        let rect = DeviceRect { x: 30, y: 15, width: 300, height: 150 };
        assert_eq!(rect.to_logical(1.5), LogicalRect { x: 20.0, y: 10.0, width: 200.0, height: 100.0 });
        assert_eq!(rect.to_logical(0.0), LogicalRect { x: 30.0, y: 15.0, width: 300.0, height: 150.0 });
        assert!(DeviceRect { width: 0, ..rect }.is_empty());
    }
}
