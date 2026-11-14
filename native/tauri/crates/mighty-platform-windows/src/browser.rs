//! Browser panes as WebView2 child webviews of the main window (Tauri's multi-webview, `unstable`), laid over the page's slot
//! (C# `MainWindow.WebShell.Panes.cs`, which places a child WebView2 by `browserBounds`). The slot comes in device pixels and
//! Tauri takes physical pixels, so the rectangle goes through unchanged.
//!
//! As C#'s `StartWebBrowserAsync` sets its engine up: new windows are refused, downloads cancelled, script dialogs never shown, and the
//! engine's own navigation events tell the host where the view went (`NavigationCompleted`: the address the engine reports, else the
//! one the navigation started with, and whether it succeeded); back and forward are the engine's; a press in the page reports the view
//! focused. The picture a page overlay shows in the view's place is the engine's `CapturePreview` (PNG). Each workspace's panes share
//! one web profile (`BrowserOptions::data_dir`, the host's `BrowserProfile` folder). As C#'s plain WebView2, the engine keeps
//! SmartScreen and a dropped file reaches the page; a "leave this page?" dialog is accepted; and a pane never loads the app's own
//! addresses (its page, its IPC, the frames' protocol), which every webview of the app would answer.

use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

use base64::Engine as _;
use mighty_platform::{
    BoxFuture, BrowserEvent, BrowserListener, BrowserOptions, BrowserSurface, DeviceRect, Drawing, NativeSurface, PetWindow, PlatformError,
    Result, SurfaceFactory, SurfaceKind, SurfaceState, TerminalOptions, TerminalSurface,
};
use tauri::webview::{DownloadEvent, NewWindowResponse, WebviewBuilder};
use tauri::{Manager as _, PhysicalPosition, PhysicalSize, Rect, Url, Webview, WebviewUrl, Window};
use webview2_com::Microsoft::Web::WebView2::Win32::{
    COREWEBVIEW2_CAPTURE_PREVIEW_IMAGE_FORMAT_PNG, COREWEBVIEW2_SCRIPT_DIALOG_KIND, COREWEBVIEW2_SCRIPT_DIALOG_KIND_BEFOREUNLOAD,
    ICoreWebView2Controller,
};
use webview2_com::{
    CapturePreviewCompletedHandler, FocusChangedEventHandler, NavigationCompletedEventHandler, NavigationStartingEventHandler,
    ScriptDialogOpeningEventHandler, take_pwstr,
};
use windows::Win32::Foundation::{HGLOBAL, HWND, POINT, RECT};
use windows::Win32::Graphics::Gdi::MapWindowPoints;
use windows::Win32::System::Com::StructuredStorage::CreateStreamOnHGlobal;
use windows::Win32::System::Com::{IStream, STREAM_SEEK_SET};
use windows::Win32::UI::WindowsAndMessaging::{GetClientRect, GetParent, IsWindowVisible};
use windows::core::{BOOL, PWSTR};

/// The address a view is made with before its first one: the engine's events are attached before anything real loads.
const BLANK: &str = "about:blank";

/// The browser engine's arguments, the same for every pane (WebView2 makes one environment per data folder, and a workspace's panes
/// share theirs: a second view with other arguments would not start). wry's default is
/// `--disable-features=msWebOOUI,msPdfOOUI,msSmartScreenProtection`; a browser pane keeps SmartScreen (as C#'s WebView2 does, with
/// no arguments), and only the mini menu and the PDF toolbar's pop-out stay off.
const BROWSER_ARGUMENTS: &str = "--disable-features=msWebOOUI,msPdfOOUI";

/// The custom protocols every webview of the app answers (the app's page `tauri`, its `ipc`, the frames' `mightyhtml`; on Windows a
/// view without `use_https_scheme`, as a pane's is, answers them at `http://<name>.<anything>`).
const APP_PROTOCOLS: [&str; 4] = ["tauri", "ipc", "mightyhtml", "asset"];

/// Whether `url` is one of the app's own addresses, which a browser pane never loads (its page there would be the app's, with what a
/// local page may call): a custom protocol of the app (as a scheme, or as the first label of an http host, which the engine hands to
/// the app's handler whatever follows), or the dev server of a dev build.
fn app_address(url: &Url, dev_server: Option<&Url>) -> bool {
    let named = |name: &str| APP_PROTOCOLS.iter().any(|p| p.eq_ignore_ascii_case(name));
    if named(url.scheme()) {
        return true;
    }
    let first_label = url.host_str().and_then(|host| host.split_once('.')).map(|(first, _)| first);
    if url.scheme() == "http" && first_label.is_some_and(named) {
        return true;
    }
    dev_server.is_some_and(|dev| {
        dev.scheme() == url.scheme() && dev.host_str() == url.host_str() && dev.port_or_known_default() == url.port_or_known_default()
    })
}

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

/// Runs `work` on the UI thread that owns the view's WebView2 and waits for its answer.
async fn on_ui<T: Send + 'static>(
    webview: &Webview,
    what: &str,
    work: impl FnOnce(&ICoreWebView2Controller) -> windows::core::Result<T> + Send + 'static,
) -> Result<T> {
    let (sender, answer) = tokio::sync::oneshot::channel();
    webview
        .with_webview(move |platform| {
            let _ = sender.send(work(&platform.controller()));
        })
        .map_err(|error| refused(what, error))?;
    answer.await.map_err(|error| refused(what, error))?.map_err(|error| refused(what, error))
}

/// C# `StartWebBrowserAsync`'s engine settings and events: script dialogs off and dismissed, navigations reported to `listener`
/// (each by the address the engine reports, else the one it started with), and the view's focus.
///
/// # Safety
/// On the UI thread that owns `controller`.
unsafe fn attach(controller: &ICoreWebView2Controller, listener: Option<BrowserListener>) -> windows::core::Result<()> {
    unsafe {
        let core = controller.CoreWebView2()?;
        core.Settings()?.SetAreDefaultScriptDialogsEnabled(false)?;
        let mut token = 0i64;
        // With the default dialogs off a handler that never accepts dismisses every alert, confirm and prompt; a page's "leave this
        // page?" is accepted, so a page cannot keep the pane from going elsewhere.
        core.add_ScriptDialogOpening(
            &ScriptDialogOpeningEventHandler::create(Box::new(|_, args| {
                if let Some(args) = args {
                    let mut kind = COREWEBVIEW2_SCRIPT_DIALOG_KIND::default();
                    args.Kind(&mut kind)?;
                    if kind == COREWEBVIEW2_SCRIPT_DIALOG_KIND_BEFOREUNLOAD {
                        args.Accept()?;
                    }
                }
                Ok(())
            })),
            &mut token,
        )?;
        let Some(listener) = listener else { return Ok(()) };
        let started: Arc<Mutex<HashMap<u64, String>>> = Arc::default();
        let starting = started.clone();
        core.add_NavigationStarting(
            &NavigationStartingEventHandler::create(Box::new(move |_, args| {
                if let Some(args) = args {
                    let mut id = 0u64;
                    args.NavigationId(&mut id)?;
                    let mut uri = PWSTR::null();
                    args.Uri(&mut uri)?;
                    starting.lock().unwrap_or_else(|p| p.into_inner()).insert(id, take_pwstr(uri));
                }
                Ok(())
            })),
            &mut token,
        )?;
        let navigated = listener.clone();
        core.add_NavigationCompleted(
            &NavigationCompletedEventHandler::create(Box::new(move |sender, args| {
                let Some(args) = args else { return Ok(()) };
                let mut id = 0u64;
                args.NavigationId(&mut id)?;
                let mut success = BOOL::default();
                args.IsSuccess(&mut success)?;
                let begun = started.lock().unwrap_or_else(|p| p.into_inner()).remove(&id);
                let source = match sender {
                    Some(engine) => {
                        let mut source = PWSTR::null();
                        engine.Source(&mut source)?;
                        take_pwstr(source)
                    }
                    None => String::new(),
                };
                let url = if source.is_empty() { begun } else { Some(source) };
                navigated.send(BrowserEvent::Navigated { url, success: success.as_bool() });
                Ok(())
            })),
            &mut token,
        )?;
        controller.add_GotFocus(
            &FocusChangedEventHandler::create(Box::new(move |_, _| {
                listener.send(BrowserEvent::Focused);
                Ok(())
            })),
            &mut token,
        )?;
        Ok(())
    }
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
            let BrowserOptions { data_dir, bounds, listener, .. } = options;
            let (popups, downloads) = (listener.clone(), listener.clone());
            // A dev build's page comes from the dev server, which a pane never loads either.
            let dev_server = if tauri::is_dev() { window.config().build.dev_url.clone() } else { None };
            if app_address(&url, dev_server.as_ref()) {
                return Err(PlatformError::Invalid("address: the app's own".into()));
            }
            // add_child waits for the UI thread: never on the UI thread itself, never on an async worker.
            let webview = tauri::async_runtime::spawn_blocking(move || {
                // Its own WebView2 data: never the page's (whose environment options differ) and never the C# app's. No remote
                // debugging port, the engine's arguments set (SmartScreen kept), files dropped on it the page's own (a drop handler
                // would swallow them), never the app's own addresses; a new window or a download is refused and the host hears of it.
                let builder = WebviewBuilder::new(label, WebviewUrl::External(Url::parse(BLANK).expect("about:blank")))
                    .data_directory(data_dir)
                    .additional_browser_args(BROWSER_ARGUMENTS)
                    .disable_drag_drop_handler()
                    .focused(false)
                    .on_navigation(move |url| !app_address(url, dev_server.as_ref()))
                    .on_new_window(move |_, _| {
                        if let Some(listener) = &popups {
                            listener.send(BrowserEvent::PopupRefused);
                        }
                        NewWindowResponse::Deny
                    })
                    .on_download(move |_, event| {
                        if matches!(event, DownloadEvent::Requested { .. })
                            && let Some(listener) = &downloads
                        {
                            listener.send(BrowserEvent::DownloadRefused);
                        }
                        false
                    });
                window.add_child(
                    builder,
                    PhysicalPosition::new(bounds.x, bounds.y),
                    PhysicalSize::new(bounds.width.max(1), bounds.height.max(1)),
                )
            })
            .await
            .map_err(|error| refused("the browser view", error))?
            .map_err(|error| refused("the browser view", error))?;
            // From here a failure closes the view it made: no shown view is left without its pane's surface.
            let started = async {
                // SAFETY: on_ui runs the closure on the UI thread that owns the controller.
                on_ui(&webview, "the browser engine", move |controller| unsafe { attach(controller, listener) }).await?;
                if url.as_str() != BLANK {
                    webview.navigate(url).map_err(|error| refused("navigate", error))?;
                }
                Ok(())
            };
            if let Err(error) = started.await {
                let _ = webview.close();
                return Err(error);
            }
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

/// Everything an `IStream` holds, from its start.
///
/// # Safety
/// On the thread that made the stream.
unsafe fn read_stream(stream: &IStream) -> windows::core::Result<Vec<u8>> {
    unsafe {
        stream.Seek(0, STREAM_SEEK_SET, None)?;
        let mut bytes = Vec::new();
        let mut buffer = vec![0u8; 65_536];
        loop {
            let mut read = 0u32;
            stream.Read(buffer.as_mut_ptr().cast(), buffer.len() as u32, Some(&mut read as *mut u32)).ok()?;
            if read == 0 {
                return Ok(bytes);
            }
            bytes.extend_from_slice(&buffer[..read as usize]);
        }
    }
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

    /// The engine's `CapturePreview` as a PNG `data:` URL: what the page shows in the slot while an overlay hides the view.
    fn snapshot(&self) -> BoxFuture<'_, Result<Option<String>>> {
        Box::pin(async move {
            let (sender, answer) = tokio::sync::oneshot::channel::<windows::core::Result<Vec<u8>>>();
            let sender = Arc::new(Mutex::new(Some(sender)));
            let started = sender.clone();
            on_ui(&self.webview, "snapshot", move |controller| {
                // SAFETY: on the UI thread that owns the controller; the stream lives in the completion closure until it is read.
                unsafe {
                    let stream = CreateStreamOnHGlobal(HGLOBAL::default(), true)?;
                    let kept = stream.clone();
                    let handler = CapturePreviewCompletedHandler::create(Box::new(move |result| {
                        let picture = result.and_then(|()| read_stream(&kept));
                        if let Some(sender) = started.lock().unwrap_or_else(|p| p.into_inner()).take() {
                            let _ = sender.send(picture);
                        }
                        Ok(())
                    }));
                    controller.CoreWebView2()?.CapturePreview(COREWEBVIEW2_CAPTURE_PREVIEW_IMAGE_FORMAT_PNG, &stream, &handler)
                }
            })
            .await?;
            let png = tokio::time::timeout(std::time::Duration::from_secs(2), answer)
                .await
                .map_err(|error| refused("snapshot", error))?
                .map_err(|error| refused("snapshot", error))?
                .map_err(|error| refused("snapshot", error))?;
            drop(sender);
            Ok((!png.is_empty()).then(|| format!("data:image/png;base64,{}", base64::engine::general_purpose::STANDARD.encode(png))))
        })
    }

    /// What Windows says, not what was last asked, read on the UI thread: the WebView2 controller's host window (wry's child
    /// HWND) in the main window's client coordinates, shown only when that window is visible and the controller is too.
    fn inspect(&self) -> BoxFuture<'_, Result<SurfaceState>> {
        Box::pin(async move {
            let (origin, client, visible) = on_ui(&self.webview, "inspect", |controller| {
                // SAFETY: the controller's and its window's getters, on the UI thread that owns them, into locals.
                unsafe {
                    let mut window = HWND::default();
                    controller.ParentWindow(&mut window)?;
                    let mut client = RECT::default();
                    GetClientRect(window, &mut client)?;
                    let mut origin = [POINT { x: client.left, y: client.top }];
                    MapWindowPoints(Some(window), Some(GetParent(window)?), &mut origin);
                    let mut visible = BOOL::default();
                    controller.IsVisible(&mut visible)?;
                    Ok((origin[0], client, IsWindowVisible(window).as_bool() && visible.as_bool()))
                }
            })
            .await?;
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
        // The engine's navigation handler refuses the app's own addresses too; this says so to the caller.
        let dev_server = if tauri::is_dev() { self.webview.config().build.dev_url.clone() } else { None };
        if app_address(&url, dev_server.as_ref()) {
            return Err(PlatformError::Invalid("address: the app's own".into()));
        }
        self.webview.navigate(url).map_err(|error| refused("navigate", error))
    }

    fn go_back(&self) -> Result<()> {
        self.webview
            .with_webview(|platform| {
                // SAFETY: on the UI thread that owns the controller.
                let _ = unsafe { platform.controller().CoreWebView2().and_then(|core| core.GoBack()) };
            })
            .map_err(|error| refused("back", error))
    }

    fn go_forward(&self) -> Result<()> {
        self.webview
            .with_webview(|platform| {
                // SAFETY: on the UI thread that owns the controller.
                let _ = unsafe { platform.controller().CoreWebView2().and_then(|core| core.GoForward()) };
            })
            .map_err(|error| refused("forward", error))
    }

    fn reload(&self) -> Result<()> {
        self.webview.reload().map_err(|error| refused("reload", error))
    }
}

/// C# `WebView2RuntimeLocator.GetRuntimeVersion`: the installed Evergreen runtime's version, or none.
pub fn runtime_version() -> Option<String> {
    use webview2_com::Microsoft::Web::WebView2::Win32::GetAvailableCoreWebView2BrowserVersionString;
    let mut version = PWSTR::null();
    // SAFETY: a null folder asks for the installed runtime; the version string is the caller's to free (take_pwstr frees it).
    unsafe { GetAvailableCoreWebView2BrowserVersionString(windows::core::PCWSTR::null(), &mut version) }.ok()?;
    Some(take_pwstr(version)).filter(|v| !v.is_empty())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn labels_are_tauri_labels_and_unique() {
        assert_eq!(label("a1:b/c", 3), "browser-a1_b_c-3");
        assert_ne!(label("p", 1), label("p", 2));
    }

    /// A browser pane keeps SmartScreen: its arguments turn off only the mini menu and the PDF pop-out (wry's default also turned
    /// off msSmartScreenProtection).
    #[test]
    fn browser_panes_keep_smartscreen() {
        assert!(!BROWSER_ARGUMENTS.contains("SmartScreen"), "{BROWSER_ARGUMENTS}");
        assert_eq!(BROWSER_ARGUMENTS, "--disable-features=msWebOOUI,msPdfOOUI");
    }

    /// The app's own addresses (its page, its IPC, the frames' protocol, the dev server) are never a pane's; the web is.
    #[test]
    fn a_pane_never_loads_the_app_itself() {
        let url = |text: &str| Url::parse(text).unwrap();
        let dev = url("http://localhost:5173");
        for own in [
            "http://tauri.localhost/index.html",
            "http://TAURI.localhost/",
            "http://ipc.localhost/host_post",
            "http://mightyhtml.h1.html.mighty.invalid/page.html",
            "tauri://localhost/",
            "ipc://localhost/",
            "mightyhtml://h1/",
            "http://localhost:5173/src/main.tsx",
        ] {
            assert!(app_address(&url(own), Some(&dev)), "{own}");
        }
        for web in [
            "https://example.com/",
            "http://localhost:8080/",
            "http://127.0.0.1:5173/",
            "about:blank",
            "https://tauri.app/",
            "http://localhost/",
        ] {
            assert!(!app_address(&url(web), Some(&dev)), "{web}");
        }
        assert!(!app_address(&url("http://localhost:5173/"), None), "a release build has no dev server");
    }

    #[test]
    fn this_computer_has_a_webview2_runtime() {
        assert!(super::runtime_version().is_some(), "the app's own page needs one");
    }
}
