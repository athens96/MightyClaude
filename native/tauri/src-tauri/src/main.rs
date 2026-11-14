//! Mighty Code's Rust + Tauri 2 host (skeleton): one window drawing the page of native/windows/web, answering it through
//! `host.rs`. It reads the saved state and never writes it.
//!
//! Identity: tauri.conf.json's `identifier` is `dev.mightycode.tauri`, a development identity so this app never shares a
//! registration, a WebView2 folder or local data with the installed C# app. It must become the real one before this ships.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod antigravity;
mod args;
mod ask;
mod cards;
mod composer;
mod delta;
mod dialogs;
mod files;
mod frame;
mod history;
mod host;
mod logfile;
mod methods;
mod mighty;
mod models;
mod panes;
mod platform;
mod runs;
mod smoke;
mod smoke_acp;
mod smoke_files;
mod smoke_frame;
mod smoke_history;
mod smoke_runs;
mod smoke_terminal;
mod surface;
mod terminal;
mod workmap;
mod workspace_create;

use std::path::Path;
use std::sync::Arc;

use mighty_core::locale::Locale;
use mighty_core::store::{self, LoadSource};
use serde_json::{Value, json};
use tauri::webview::PageLoadEvent;
use tauri::{Manager, RunEvent, WebviewUrl, WebviewWindowBuilder};

use crate::host::{Host, WINDOW};
use crate::logfile::LogFile;
use crate::smoke::Smoke;
use crate::surface::SurfaceManager;

/// Page → host: one message (`{kind:"call", ...}`), queued for the dispatcher; the answer comes back on `host-message`. Only the
/// page's own webview showing the app's own page is heard (C# `WebShell.Trusted(args.Source)`): a browser pane's child webview, or
/// the page's webview somewhere else, never reaches the host.
#[tauri::command]
fn host_post(message: Value, webview: tauri::Webview, inbound: tauri::State<'_, Inbound>, origin: tauri::State<'_, AppOrigin>) {
    if webview.label() == WINDOW && webview.url().is_ok_and(|url| origin.trusts(&url)) {
        let _ = inbound.0.send(message);
    }
}

/// Where the app's own page is served (tauri's `get_app_url`): the dev server in a dev build, else the `tauri` protocol
/// (`http://tauri.localhost` on Windows, `tauri://localhost` on the Mac).
struct AppOrigin(tauri::Url);

impl AppOrigin {
    fn new(config: &tauri::Config) -> AppOrigin {
        #[cfg(dev)]
        if let Some(url) = config.build.dev_url.clone() {
            return AppOrigin(url);
        }
        let _ = config;
        let base = if cfg!(windows) { "http://tauri.localhost" } else { "tauri://localhost" };
        AppOrigin(tauri::Url::parse(base).expect("the tauri protocol's address"))
    }

    /// C# `WebShell.Trusted`: an address on the app's own origin (scheme, host and port; `tauri://` has no web origin to compare).
    fn trusts(&self, url: &tauri::Url) -> bool {
        url.scheme() == self.0.scheme()
            && url.host_str() == self.0.host_str()
            && url.port_or_known_default() == self.0.port_or_known_default()
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
    // C# StateStore: the state loaded (and, only with --allow-state-write, the damaged file put aside and the legacy one copied);
    // its notes in the OS's language, as C# loads before the saved preference is applied. A file that does not read stops every
    // save: the app starts empty in memory and says why.
    let legacy = legacy.into_iter().find(|dir| dir.join(store::STATE_FILE).exists());
    let mut state = store::Store::new(data_dir.clone(), legacy, options.allow_state_write);
    let loaded = state.load(&Locale::new(methods::chosen_language("system")));
    let (snapshot, failure) = match loaded {
        Ok(snapshot) => (snapshot, None),
        Err(error) => (mighty_core::AppSnapshot::default(), Some(error)),
    };
    let locale = Locale::new(methods::chosen_language(snapshot.language_preference()));
    let banner = match (&state.load_notice, &failure) {
        (Some(notice), _) => Some(notice.clone()),
        (None, Some(error)) => Some(format!("{} ({error})", error.message(&locale))),
        _ => None,
    };
    let source = state.source.clone().unwrap_or(LoadSource::Empty);
    let load = json!({
        "path": state.state_path(),
        "source": source_name(&source),
        "notice": state.load_notice,
        "movedAsideTo": state.aside,
        "error": failure.as_ref().map(|e| e.to_string()),
        "writes": options.allow_state_write,
        "workspaces": snapshot.workspaces().len(),
        "panes": snapshot.sessions().len(),
    });
    log.line(&format!(
        "start {} profile {} load {} ({} workspaces, {} panes) language {} writes {}",
        env!("CARGO_PKG_VERSION"),
        data_dir.display(),
        source_name(&source),
        snapshot.workspaces().len(),
        snapshot.sessions().len(),
        locale.language(),
        if options.allow_state_write { "on" } else { "off" }
    ));
    if let Some(error) = &failure {
        log.line(&format!("load: {error}"));
    }
    if failure.is_some() {
        state.snapshot = snapshot.clone();
    }
    #[cfg(windows)]
    {
        state.replace = host::replace_file;
    }
    let smoke = options.smoke_test.then(|| {
        let mode = match (&options.smoke_fake_cli, options.smoke_real_claude) {
            (Some(_), _) => smoke::Mode::FakeClaude,
            (None, true) => smoke::Mode::RealClaude,
            (None, false) => smoke::Mode::Page,
        };
        let record = options.log_dir.clone().unwrap_or_else(|| data_dir.join("tauri-logs")).join("fake-claude").join("replay");
        Smoke::new(
            options.smoke_out.clone().unwrap_or_else(|| data_dir.join("smoke-result.json")),
            options.smoke_exit,
            load.clone(),
            mode,
            options.smoke_fake_cli.is_some().then_some(record),
        )
    });
    let theme = snapshot.theme().to_string();
    let mut parts = Some((state, banner, log, smoke));
    let debugging_port = options.remote_debugging_port;
    // The smoke's stand-in Claude records what it was sent beside the smoke's logs.
    let fake_claude = options.smoke_fake_cli.clone().map(|binary| runs::FakeClaude {
        binary,
        recording: smoke::RECORDING.into(),
        record: options.log_dir.clone().unwrap_or_else(|| data_dir.join("tauri-logs")).join("fake-claude").join("replay"),
    });
    // The picture cache: the profile's own when the app may write it, else this machine's local folder for the profile.
    let image_dir = if options.allow_state_write {
        platform.paths.image_cache_dir()
    } else {
        platform.paths.local_dir().join("image-cache").join(platform.paths.profile_key())
    };
    // The CLIs' catalogue's storage (C# ProviderCatalog's storageDirectory: the last list each CLI gave, `model-lists.json`, and beside
    // it the picker the last Hermes session offered, `hermes-models.json`) likewise: the profile folder, else this machine's local folder
    // for the profile.
    let catalog_dir = if options.allow_state_write {
        data_dir.clone()
    } else {
        platform.paths.local_dir().join("provider-catalog").join(platform.paths.profile_key())
    };
    // The work map's summaries likewise, and its git's empty hooks folder this machine's own (workmap.rs).
    let workmap_folders = workmap::Folders::new(platform.paths.as_ref(), options.allow_state_write);
    // The Antigravity rules' ledger (antigravity.rs, C# `AntigravityRuleLeases`) likewise.
    let antigravity_ledger =
        antigravity::ledger_path(&data_dir, platform.paths.local_dir(), platform.paths.profile_key(), options.allow_state_write);
    let (local_dir, profile_key, allow_state_write) =
        (platform.paths.local_dir().to_path_buf(), platform.paths.profile_key().to_string(), options.allow_state_write);
    // The file originals (files.rs, C# `FileOriginalStore`) likewise: the profile's `file-originals`, else this machine's own.
    let originals_dir = if options.allow_state_write {
        platform.paths.file_originals_dir()
    } else {
        platform.paths.local_dir().join("file-originals").join(platform.paths.profile_key())
    };

    let app = tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .invoke_handler(tauri::generate_handler![
            host_post,
            smoke_probe,
            smoke::smoke_surface,
            smoke_runs::smoke_step,
            smoke_terminal::smoke_terminal,
            smoke_history::smoke_history
        ])
        // HTML previews' folders, each under a host of its own, for the page's sandboxed frame (files.rs).
        .register_asynchronous_uri_scheme_protocol(files::PROTOCOL, files::protocol)
        .setup(move |app| {
            let (state, banner, log, smoke) = parts.take().expect("setup runs once");
            let version = app.package_info().version.to_string();
            let host = Host::new(
                app.handle().clone(),
                data_dir.clone(),
                platform.processes.clone(),
                state,
                locale,
                version,
                banner,
                log,
                smoke,
                methods::registry(),
            );
            // The files pane's state and the file originals, which the run engine keeps through it (files.rs).
            files::start(&host, originals_dir.clone());
            workmap::configure(&host, workmap_folders.clone());
            // The frame's seams (frame.rs): the add menu's files entry opens files.rs's pane, and an agent pane added from the menu
            // offers the folder's earlier sessions through history.rs.
            let _ = frame::OPEN_FILES.set(files::open_files);
            let _ = frame::RESUME.set(Arc::new(history::HistoryResume));
            // The run engine: the run service owns the state from here on (runs.rs).
            if let Some(fake) = &fake_claude
                && let Some(folder) = fake.record.parent()
            {
                let _ = std::fs::create_dir_all(folder);
            }
            // The smoke's stand-in agy writes its own settings, never the user's, and keeps its ledger beside them.
            let agy_settings = fake_claude.as_ref().map(runs::FakeClaude::agy_settings);
            let agy_ledger = match agy_settings.as_deref().and_then(Path::parent).and_then(Path::parent).and_then(Path::parent) {
                Some(home) => home.join(antigravity::LEDGER),
                None => antigravity_ledger.clone(),
            };
            // The user's own settings: what a launch with `--allow-state-write` the other way round recorded goes too (C4), the profile's
            // ledger read, never written, without the flag.
            let other_ledger =
                agy_settings.is_none().then(|| antigravity::ledger_path(&data_dir, &local_dir, &profile_key, !allow_state_write));
            let agy_rules = antigravity::rules(
                agy_settings.as_deref(),
                &agy_ledger,
                other_ledger.as_deref().map(|other| (other, allow_state_write)),
                &[data_dir.clone(), local_dir.clone()],
            );
            runs::Runs::start(
                &host,
                platform.processes.clone(),
                platform.environment.clone(),
                fake_claude.clone(),
                image_dir.clone(),
                catalog_dir.clone(),
                agy_rules,
            );
            // Terminal panes, the agents' terminals, terminal prompts and shell block runs (terminal.rs).
            terminal::Terminals::start(&host, &platform);
            host.start_clocks();
            let (inbound, mut calls) = tokio::sync::mpsc::unbounded_channel::<Value>();
            app.manage(Inbound(inbound));
            app.manage(host.clone());
            // The user's settings outside the state (the text setting: frame.rs).
            app.manage(platform.prefs.clone());
            let dispatcher = host.clone();
            tauri::async_runtime::spawn(async move {
                while let Some(message) = calls.recv().await {
                    // Each call is a task of its own (C#'s `async void Receive`); the next starts once this one first returned, so
                    // what each does at once happens in the order the page posted them (host.rs).
                    let (first, returned) = tokio::sync::oneshot::channel();
                    let host = dispatcher.clone();
                    tauri::async_runtime::spawn(async move {
                        let surfaces = surfaces(&host.app);
                        // NativeSlot: who draws what reaches the page before `ready` pushes the snapshot (surface.rs).
                        if message.get("method").and_then(Value::as_str) == Some("ready")
                            && let Some(surfaces) = &surfaces
                        {
                            surfaces.announce();
                        }
                        let ready = message.get("method").and_then(Value::as_str) == Some("ready");
                        host.clone().dispatch(message, first).await;
                        // A page that loaded again gets back its open dialogs and the text setting (frame.rs).
                        if ready {
                            frame::page_ready(&host);
                        }
                        // A pane the call closed takes its native view with it.
                        if let Some(surfaces) = surfaces {
                            surfaces.retain(&surface::live_panes(&host)).await;
                        }
                    });
                    let _ = returned.await;
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
            let origin = AppOrigin::new(app.config());
            let navigation = AppOrigin(origin.0.clone());
            app.manage(origin);
            let mut window = WebviewWindowBuilder::new(app, WINDOW, WebviewUrl::App("index.html".into()))
                .title("Mighty Code")
                .inner_size(1440.0, 920.0)
                .min_inner_size(940.0, 650.0)
                .background_color(tauri::window::Color(r, g, b, 255))
                .data_directory(webview_data)
                // The page takes dropped files itself (HTML5 drop → addAttachments), as C#'s WebView2 does; Tauri's own handler
                // would swallow the drop on Windows.
                .disable_drag_drop_handler()
                // C# WebShell: the page never leaves the app's own origin (a stray drop or link would navigate it away), and it
                // opens no window of its own.
                .on_navigation(move |url| navigation.trusts(url))
                .on_new_window(|_, _| tauri::webview::NewWindowResponse::Deny)
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
                // Every terminal closed first (C# CloseTerminalsAsync), the wait bounded.
                terminal::close_all(app);
                // The last save (C# DesktopService.DisposeAsync), written only with --allow-state-write.
                host.close();
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

#[cfg(test)]
mod tests {
    use super::*;

    fn url(text: &str) -> tauri::Url {
        tauri::Url::parse(text).unwrap()
    }

    /// C# `WebShell.Trusted`: the app's own origin only, however a page elsewhere names itself.
    #[test]
    fn only_the_apps_own_page_is_trusted() {
        let windows = AppOrigin(url("http://tauri.localhost"));
        assert!(windows.trusts(&url("http://tauri.localhost/index.html")));
        assert!(windows.trusts(&url("http://TAURI.localhost:80/a?b#c")));
        for elsewhere in [
            "https://tauri.localhost/",
            "http://tauri.localhost:8080/",
            "http://tauri.localhost.example.com/",
            "http://localhost/",
            "file:///C:/Users/me/Downloads/page.html",
            "https://example.com/",
            "about:blank",
        ] {
            assert!(!windows.trusts(&url(elsewhere)), "{elsewhere}");
        }
        let mac = AppOrigin(url("tauri://localhost"));
        assert!(mac.trusts(&url("tauri://localhost/index.html")));
        assert!(!mac.trusts(&url("tauri://elsewhere/index.html")));
        let dev = AppOrigin(url("http://localhost:5173"));
        assert!(dev.trusts(&url("http://localhost:5173/src/main.tsx")));
        assert!(!dev.trusts(&url("http://localhost:5174/")));
    }
}
