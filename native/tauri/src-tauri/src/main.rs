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
mod smoke;

use std::path::{Path, PathBuf};
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

/// Page → host: one message (`{kind:"call", ...}`), queued for the dispatcher; the answer comes back on `host-message`.
#[tauri::command]
fn host_post(message: Value, inbound: tauri::State<'_, Inbound>) {
    let _ = inbound.0.send(message);
}

/// The smoke's probe script hands what it read of the page here (smoke.rs).
#[tauri::command]
fn smoke_probe(probe: Value, host: tauri::State<'_, Arc<Host>>) {
    if let Some(smoke) = &host.smoke {
        smoke.finish(&host, probe);
    }
}

struct Inbound(tokio::sync::mpsc::UnboundedSender<Value>);

/// A folder name for a profile path, so each profile gets its own WebView2 data.
fn folder_name(path: &Path) -> String {
    let name: String = path.to_string_lossy().chars().map(|c| if c.is_ascii_alphanumeric() || c == '-' { c } else { '_' }).collect();
    name.chars().rev().take(80).collect::<Vec<_>>().into_iter().rev().collect()
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
    let Some(data_dir) = options.profile.clone().or_else(store::default_data_dir) else {
        eprintln!("No data folder (%APPDATA%) is known.");
        std::process::exit(1);
    };
    // An explicit profile never reads the app's folders from before the rename (C# Program.cs).
    let legacy = if options.profile.is_none() { store::legacy_data_dirs() } else { vec![] };
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
    let profile = options.profile.clone();
    let debugging_port = options.remote_debugging_port;

    let app = tauri::Builder::default()
        .invoke_handler(tauri::generate_handler![host_post, smoke_probe])
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
                    dispatcher.clone().dispatch(message).await;
                }
            });
            if host.smoke.is_some() {
                Smoke::watch(host.clone());
            }

            // Its own WebView2 data, never the C# app's (<profile>\web-profile): under this identity's local data folder, one
            // folder per profile.
            let webview_data: PathBuf = app
                .path()
                .app_local_data_dir()?
                .join("webview")
                .join(profile.as_deref().map(folder_name).unwrap_or_else(|| "default".into()));
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
                    }
                });
            if let Some(port) = debugging_port {
                // These replace wry's own arguments, which are kept.
                window = window.additional_browser_args(&format!(
                    "--disable-features=msWebOOUI,msPdfOOUI,msSmartScreenProtection --remote-debugging-port={port}"
                ));
            }
            window.build()?;
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("the Tauri app");
    app.run(|app, event| {
        if let RunEvent::Exit = event
            && let Some(host) = app.try_state::<Arc<Host>>()
        {
            host.write_unimplemented();
            host.log.line("exit");
        }
    });
}
