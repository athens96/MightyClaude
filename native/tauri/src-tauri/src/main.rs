//! Mighty Code's Rust + Tauri 2 host (skeleton): one window drawing the page of native/windows/web, answering it through
//! `host.rs`. It reads the saved state and never writes it.
//!
//! Identity: tauri.conf.json's `identifier` is `dev.mightycode.tauri`, a development identity so this app never shares a
//! registration, a WebView2 folder or local data with the installed C# app. It must become the real one before this ships.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod args;
mod host;
mod logfile;
mod methods;
mod platform;
mod smoke;
mod surface;

use std::sync::Arc;

use mighty_core::locale::Locale;
use mighty_core::normalize;
use mighty_core::store::{self, LoadSource};
use serde_json::{Value, json};
use tauri::webview::PageLoadEvent;
use tauri::{Manager, RunEvent, WebviewUrl, WebviewWindowBuilder};

use crate::host::{Host, WINDOW};
use crate::logfile::LogFile;
use crate::smoke::Smoke;
use crate::surface::SurfaceManager;

/// Page → host: one message (`{kind:"call", ...}`), queued for the dispatcher; the answer comes back on `host-message`. Only the
/// page's own webview is heard: a browser pane's child webview never reaches the host.
#[tauri::command]
fn host_post(message: Value, webview: tauri::Webview, inbound: tauri::State<'_, Inbound>) {
    if webview.label() == WINDOW {
        let _ = inbound.0.send(message);
    }
}

/// The smoke's probe script hands what it read of the page here (smoke.rs).
#[tauri::command]
fn smoke_probe(probe: Value, host: tauri::State<'_, Arc<Host>>) {
    if let Some(smoke) = &host.smoke {
        smoke.probed(&host, probe);
    }
}

struct Inbound(tokio::sync::mpsc::UnboundedSender<Value>);

fn surfaces(app: &tauri::AppHandle) -> Option<Arc<SurfaceManager>> {
    app.try_state::<Arc<SurfaceManager>>().map(|state| state.inner().clone())
}

fn source_name(source: &LoadSource) -> String {
    match source {
        LoadSource::State => "state".into(),
        LoadSource::Backup => "backup".into(),
        LoadSource::Legacy(path) => format!("legacy {}", path.display()),
        LoadSource::Empty => "empty".into(),
        LoadSource::DamagedEmpty => "damaged, no backup: empty".into(),
        LoadSource::Unreadable => "unreadable: empty".into(),
    }
}

fn main() {
    let options = match args::parse(std::env::args().skip(1)) {
        Ok(options) => options,
        Err(error) => {
            eprintln!("{error}");
            std::process::exit(1);
        }
    };
    let context = tauri::generate_context!();
    // The OS's ports (platform.rs). Their paths are the data folder (`--profile` or %APPDATA%\MightyCode) and, for the default
    // profile only, the folders from before the rename (C# Program.cs).
    let platform = match platform::platform(options.profile.clone(), &context.config().identifier) {
        Ok(platform) => platform,
        Err(error) => {
            eprintln!("{error}");
            std::process::exit(1);
        }
    };
    let data_dir = platform.paths.data_dir().to_path_buf();
    let legacy = platform.paths.legacy_data_dirs().to_vec();
    let log = LogFile::new(options.log_dir.clone().unwrap_or_else(|| data_dir.join("tauri-logs")));
    let report = store::load(&data_dir, &legacy);
    let snapshot = normalize::for_page(&report.snapshot);
    let locale = Locale::new(methods::chosen_language(snapshot.language_preference()));
    let banner = match (&report.notice_key, &report.error) {
        (Some(key), _) => Some(locale.get_with(key, &[("file", report.aside.as_deref().unwrap_or(""))])),
        (None, Some(error)) => Some(format!("{} ({error})", locale.get(error.locale_key()))),
        _ => None,
    };
    let load = json!({
        "path": report.path,
        "source": source_name(&report.source),
        "notice": report.notice_key,
        "wouldMoveAsideTo": report.aside,
        "error": report.error.as_ref().map(|e| e.to_string()),
        "workspaces": report.snapshot.workspaces.len(),
        "panes": report.snapshot.sessions.len(),
    });
    log.line(&format!(
        "start {} profile {} load {} ({} workspaces, {} panes) language {}",
        env!("CARGO_PKG_VERSION"),
        data_dir.display(),
        source_name(&report.source),
        snapshot.workspaces.len(),
        snapshot.sessions.len(),
        locale.language()
    ));
    if let Some(error) = &report.error {
        log.line(&format!("load: {error}"));
    }
    let smoke = options.smoke_test.then(|| {
        Smoke::new(options.smoke_out.clone().unwrap_or_else(|| data_dir.join("smoke-result.json")), options.smoke_exit, load.clone())
    });
    let theme = snapshot.theme().to_string();
    let mut parts = Some((snapshot, banner, log, smoke));
    let debugging_port = options.remote_debugging_port;

    let app = tauri::Builder::default()
        .invoke_handler(tauri::generate_handler![host_post, smoke_probe, smoke::smoke_surface])
        .setup(move |app| {
            let (snapshot, banner, log, smoke) = parts.take().expect("setup runs once");
            let version = app.package_info().version.to_string();
            let host =
                Host::new(app.handle().clone(), data_dir.clone(), snapshot, locale, version, banner, log, smoke, methods::registry());
            let (inbound, mut calls) = tokio::sync::mpsc::unbounded_channel::<Value>();
            app.manage(Inbound(inbound));
            app.manage(host.clone());
            let dispatcher = host.clone();
            tauri::async_runtime::spawn(async move {
                while let Some(message) = calls.recv().await {
                    let surfaces = surfaces(&dispatcher.app);
                    // NativeSlot: who draws what reaches the page before `ready` pushes the snapshot (surface.rs).
                    if message.get("method").and_then(Value::as_str) == Some("ready")
                        && let Some(surfaces) = &surfaces
                    {
                        surfaces.announce();
                    }
                    dispatcher.clone().dispatch(message).await;
                    // A pane the call closed takes its native view with it.
                    if let Some(surfaces) = surfaces {
                        surfaces.retain(&surface::live_panes(&dispatcher)).await;
                    }
                }
            });
            if host.smoke.is_some() {
                Smoke::watch(host.clone());
            }

            // Its own WebView2 data, never the C# app's (<profile>\web-profile): under this identity's local data folder, one
            // folder per profile (Paths::webview_dir).
            let webview_data = platform.paths.webview_dir();
            host.log.line(&format!("webview data {}", webview_data.display()));
            let (r, g, b) = if theme == "light" { (0xEC, 0xEE, 0xF3) } else { (0x0B, 0x0F, 0x19) };
            let page = host.clone();
            let mut window = WebviewWindowBuilder::new(app, WINDOW, WebviewUrl::App("index.html".into()))
                .title("Mighty Code")
                .inner_size(1440.0, 920.0)
                .min_inner_size(940.0, 650.0)
                .background_color(tauri::window::Color(r, g, b, 255))
                .data_directory(webview_data)
                .on_page_load(move |_, payload| {
                    if payload.event() == PageLoadEvent::Started {
                        page.page_started();
                        // On the UI thread: the views hide from a task, never here (a view may be waiting for this thread).
                        if let Some(surfaces) = surfaces(&page.app) {
                            tauri::async_runtime::spawn(async move { surfaces.page_started().await });
                        }
                    }
                });
            if let Some(port) = debugging_port {
                // These replace wry's own arguments, which are kept.
                window = window.additional_browser_args(&format!(
                    "--disable-features=msWebOOUI,msPdfOOUI,msSmartScreenProtection --remote-debugging-port={port}"
                ));
            }
            let window = window.build()?;
            // The native views over the page (browser panes; the Mac's terminal later), in the OS's implementation.
            let factory = platform::surfaces(window.as_ref().window());
            let browser_data = platform.paths.browser_dir();
            host.log.line(&format!("browser data {}", browser_data.display()));
            let push = surface::page_push(app.handle().clone());
            app.manage(Arc::new(SurfaceManager::new(factory, platform.profile.os, browser_data, push)));
            Ok(())
        })
        .build(context)
        .expect("the Tauri app");
    // Tauri's event loop ends with 0 whatever `exit(code)` asked (tauri-runtime-wry's RequestExit sets ControlFlow::Exit): the
    // code is kept here and given to the process at the end, so a failed `--smoke-exit` run exits 1.
    let mut exit_code = 0;
    app.run(move |app, event| match event {
        RunEvent::ExitRequested { code: Some(code), .. } => exit_code = code,
        RunEvent::Exit => {
            if let Some(host) = app.try_state::<Arc<Host>>() {
                host.write_unimplemented();
                host.log.line("exit");
            }
            if exit_code != 0 {
                app.cleanup_before_exit();
                std::process::exit(exit_code);
            }
        }
        _ => {}
    });
}
