//! Browser panes as WebView2 child webviews of the main window (Tauri's multi-webview, `unstable`), laid over the page's slot
//! (C# `MainWindow.WebShell.Panes.cs`, which places a child WebView2 by `browserBounds`). The slot comes in device pixels and
//! Tauri takes physical pixels, so the rectangle goes through unchanged.
//!
//! W1 makes navigation, placement, hiding and closing work. Back/forward state, popups, downloads, per-workspace profiles and the
//! picture shown while a page overlay hides the view are W4's (`snapshot` answers None until then).

use std::sync::atomic::{AtomicU64, Ordering};

use mighty_platform::{
    BoxFuture, BrowserOptions, BrowserSurface, DeviceRect, Drawing, NativeSurface, PetWindow, PlatformError, Result, SurfaceFactory,
    SurfaceKind, SurfaceState, TerminalOptions, TerminalSurface,
};
use tauri::webview::WebviewBuilder;
use tauri::{PhysicalPosition, PhysicalSize, Rect, Url, Webview, WebviewUrl, Window};
use windows::Win32::Foundation::{HWND, POINT, RECT};
use windows::Win32::Graphics::Gdi::MapWindowPoints;
use windows::Win32::UI::WindowsAndMessaging::{GetClientRect, GetParent, IsWindowVisible};

fn refused(what: &str, error: impl std::fmt::Display) -> PlatformError {
    PlatformError::Refused(format!("{what}: {error}"))
}

/// The native views of one window.
pub struct WebviewSurfaces {
    window: Window,
    /// Makes each webview's label unique, also for a pane that opens again before its old view is gone.
    next: AtomicU64,
}

impl WebviewSurfaces {
    pub fn new(window: Window) -> WebviewSurfaces {
        WebviewSurfaces { window, next: AtomicU64::new(1) }
    }
}

/// A Tauri label (letters, digits, `-`, `_`) for a pane's webview.
fn label(pane_id: &str, serial: u64) -> String {
    let pane: String = pane_id.chars().take(64).map(|c| if c.is_ascii_alphanumeric() || c == '-' { c } else { '_' }).collect();
    format!("browser-{pane}-{serial}")
}

impl SurfaceFactory for WebviewSurfaces {
    fn drawing(&self, kind: SurfaceKind) -> Drawing {
        match kind {
            SurfaceKind::Terminal => Drawing::Web,
            SurfaceKind::Browser => Drawing::Native,
        }
    }

    fn open_browser(&self, options: BrowserOptions) -> BoxFuture<'_, Result<Box<dyn BrowserSurface>>> {
        Box::pin(async move {
            let url = Url::parse(&options.url).map_err(|error| PlatformError::Invalid(format!("address: {error}")))?;
            let label = label(&options.pane_id, self.next.fetch_add(1, Ordering::Relaxed));
            let window = self.window.clone();
            let BrowserOptions { data_dir, bounds, .. } = options;
            // add_child waits for the UI thread: never on the UI thread itself, never on an async worker.
            let webview = tauri::async_runtime::spawn_blocking(move || {
                // Its own WebView2 data: never the page's (whose environment options differ) and never the C# app's.
                let builder = WebviewBuilder::new(label, WebviewUrl::External(url)).data_directory(data_dir).focused(false);
                window.add_child(
                    builder,
                    PhysicalPosition::new(bounds.x, bounds.y),
                    PhysicalSize::new(bounds.width.max(1), bounds.height.max(1)),
                )
            })
            .await
            .map_err(|error| refused("the browser view", error))?
            .map_err(|error| refused("the browser view", error))?;
            Ok(Box::new(WebviewBrowser { webview }) as Box<dyn BrowserSurface>)
        })
    }

    fn open_terminal(&self, _options: TerminalOptions) -> BoxFuture<'_, Result<Box<dyn TerminalSurface>>> {
        // Windows draws terminals in the page (xterm.js over ConPTY).
        Box::pin(async { Err(PlatformError::unsupported("SurfaceFactory", "open_terminal")) })
    }

    fn open_pet(&self) -> BoxFuture<'_, Result<Box<dyn PetWindow>>> {
        Box::pin(async { Err(PlatformError::unsupported("SurfaceFactory", "open_pet")) })
    }
}

struct WebviewBrowser {
    webview: Webview,
}

impl NativeSurface for WebviewBrowser {
    fn place(&self, rect: DeviceRect) -> Result<()> {
        let bounds =
            Rect { position: PhysicalPosition::new(rect.x, rect.y).into(), size: PhysicalSize::new(rect.width, rect.height).into() };
        self.webview.set_bounds(bounds).map_err(|error| refused("place", error))
    }

    fn set_visible(&self, visible: bool) -> Result<()> {
        if visible { self.webview.show() } else { self.webview.hide() }.map_err(|error| refused("show", error))
    }

    fn focus(&self) -> Result<()> {
        self.webview.set_focus().map_err(|error| refused("focus", error))
    }

    fn snapshot(&self) -> BoxFuture<'_, Result<Option<String>>> {
        Box::pin(async { Ok(None) })
    }

    /// What Windows says, not what was last asked, read on the UI thread: the WebView2 controller's host window (wry's child
    /// HWND) in the main window's client coordinates, shown only when that window is visible and the controller is too.
    fn inspect(&self) -> BoxFuture<'_, Result<SurfaceState>> {
        Box::pin(async move {
            let (sender, answer) = tokio::sync::oneshot::channel();
            self.webview
                .with_webview(move |platform| {
                    let controller = platform.controller();
                    // SAFETY: the controller's and its window's getters, on the UI thread that owns them, into locals.
                    let read = unsafe {
                        (|| {
                            let mut window = HWND::default();
                            controller.ParentWindow(&mut window)?;
                            let mut client = RECT::default();
                            GetClientRect(window, &mut client)?;
                            let mut origin = [POINT { x: client.left, y: client.top }];
                            MapWindowPoints(Some(window), Some(GetParent(window)?), &mut origin);
                            let mut visible = windows::core::BOOL::default();
                            controller.IsVisible(&mut visible)?;
                            Ok::<_, windows::core::Error>((origin[0], client, IsWindowVisible(window).as_bool() && visible.as_bool()))
                        })()
                    };
                    let _ = sender.send(read);
                })
                .map_err(|error| refused("inspect", error))?;
            let (origin, client, visible) =
                answer.await.map_err(|error| refused("inspect", error))?.map_err(|error| refused("inspect", error))?;
            let bounds = DeviceRect {
                x: origin.x,
                y: origin.y,
                width: u32::try_from(client.right - client.left).unwrap_or(0),
                height: u32::try_from(client.bottom - client.top).unwrap_or(0),
            };
            Ok(SurfaceState { bounds, visible, url: self.webview.url().ok().map(String::from) })
        })
    }

    fn close(&self) -> Result<()> {
        self.webview.close().map_err(|error| refused("close", error))
    }
}

impl BrowserSurface for WebviewBrowser {
    fn navigate(&self, url: &str) -> Result<()> {
        let url = Url::parse(url).map_err(|error| PlatformError::Invalid(format!("address: {error}")))?;
        self.webview.navigate(url).map_err(|error| refused("navigate", error))
    }

    fn go_back(&self) -> Result<()> {
        self.webview.eval("history.back()").map_err(|error| refused("back", error))
    }

    fn go_forward(&self) -> Result<()> {
        self.webview.eval("history.forward()").map_err(|error| refused("forward", error))
    }

    fn reload(&self) -> Result<()> {
        self.webview.reload().map_err(|error| refused("reload", error))
    }
}

#[cfg(test)]
mod tests {
    use super::label;

    #[test]
    fn labels_are_tauri_labels_and_unique() {
        assert_eq!(label("a1:b/c", 3), "browser-a1_b_c-3");
        assert_ne!(label("p", 1), label("p", 2));
    }
}
