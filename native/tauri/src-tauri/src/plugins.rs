//! The plugin browser, dialog D3, and `openPlugins` (C# MainWindow.Plugins.cs:50-469, MainWindow.WebShell.Shell.cs:84-88). Owner: W6-D.
//!
//! One window serves both providers — Claude through `ClaudePluginReader`, Codex through `CodexPluginReader`, both on the app's one
//! `PluginOperations` (updates.rs) — as the page's panel `plugins` (the popup standard's kind C): the title and the workspace in its
//! header with the CLI's version and the reload as its tools, its ✕ dimmed while a change runs (`busy = !browser.can_close()`), the
//! ring, what is being done and the cancel in its footer. Every decision (which rows, what each says, what is enabled) is
//! mighty-updates' `ClaudePluginBrowser`'s; the builders are `mighty_updates::view`'s. The only two changes it offers are the two
//! the Mac has: install one plugin, refresh the registered marketplaces.
//!
//! A remote workspace's plugins are its host's (C#'s refusal). A real install or refresh needs `--allow-state-write` until W9
//! (`Host::may_change_machine`); the smoke installs through a fake reader and never starts a CLI.

use std::collections::HashMap;
use std::sync::{Arc, Mutex, MutexGuard};

use mighty_core::model::Workspace;
use mighty_native::surface::DialogChrome;
use mighty_native::surface::DialogSpec;
use mighty_native::ui::Content;
use mighty_run::strings::get;
use mighty_updates::plugins::{ClaudePluginBrowser, ClaudePluginOperationResult, ClaudePluginSnapshot, PluginReader, status};
use mighty_updates::runner::CancellationToken;
use mighty_updates::view::{self, PluginActions};

use crate::host::Host;

/// A smoke's stand-in for the list read (C# `smokePluginRead`, `smokeCodexPluginRead`).
pub type SmokeRead = Arc<dyn Fn(&Workspace) -> ClaudePluginSnapshot + Send + Sync>;
/// A smoke's stand-in reader (C# `smokeReaderFactory`): no CLI starts.
pub type SmokeReaderFactory = Arc<dyn Fn(&str) -> Arc<dyn PluginReader> + Send + Sync>;

/// The smoke's hooks, and the window open now (the smoke reads its browser).
#[derive(Default)]
struct Hooks {
    read: HashMap<String, SmokeRead>,
    reader: Option<SmokeReaderFactory>,
    open: Option<(Arc<ClaudePluginBrowser>, String)>,
}

static HOOKS: Mutex<Option<Hooks>> = Mutex::new(None);

fn hooks() -> MutexGuard<'static, Option<Hooks>> {
    HOOKS.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn with_hooks<T>(f: impl FnOnce(&mut Hooks) -> T) -> T {
    f(hooks().get_or_insert_with(Hooks::default))
}

/// The smoke's list read for `provider` (None: the reader's).
pub fn set_smoke_read(provider: &str, read: Option<SmokeRead>) {
    with_hooks(|hooks| match read {
        Some(read) => {
            hooks.read.insert(provider.to_string(), read);
        }
        None => {
            hooks.read.remove(provider);
        }
    });
}

/// The smoke's reader factory (None: the app's readers).
pub fn set_smoke_reader(factory: Option<SmokeReaderFactory>) {
    with_hooks(|hooks| hooks.reader = factory);
}

/// The plugin window open now: its browser and its dialog's id.
pub fn open_window() -> Option<(Arc<ClaudePluginBrowser>, String)> {
    with_hooks(|hooks| hooks.open.clone())
}

/// One open window: its browser, reader and dialog.
struct Panel {
    host: Arc<Host>,
    provider: String,
    workspace: Workspace,
    browser: Arc<ClaudePluginBrowser>,
    reader: Arc<dyn PluginReader>,
    /// The reader is the smoke's stand-in: nothing it does reaches a CLI.
    fake: bool,
    id: Mutex<Option<String>>,
    closing: CancellationToken,
}

impl Panel {
    /// C# `RenderPlugins` after work done outside an input: the dialog read again now.
    fn touch(&self) {
        if let Some(id) = self.id.lock().unwrap_or_else(|p| p.into_inner()).clone() {
            crate::native::touch(&self.host, &id);
        }
    }

    /// C# `LoadPluginsAsync`: once at a time; while a CLI update runs, its sentence; in a smoke with a stand-in read, the fixture.
    fn load(self: &Arc<Self>) {
        if self.browser.loading() {
            return;
        }
        self.browser.begin_load();
        self.touch();
        let panel = self.clone();
        tauri::async_runtime::spawn(async move {
            let stand_in = with_hooks(|hooks| hooks.read.get(&panel.provider).cloned());
            let snapshot: Arc<ClaudePluginSnapshot> = if crate::updates::any_running(&panel.host) {
                Arc::new(ClaudePluginSnapshot::of(status::BUSY, get("loginRecovery.updating")))
            } else if let Some(read) = stand_in {
                Arc::new(read(&panel.workspace))
            } else {
                panel.reader.snapshot(&panel.workspace, panel.closing.clone()).await
            };
            if panel.host.closing() {
                return;
            }
            panel.browser.apply(snapshot);
            panel.touch();
        });
    }

    /// C# `PluginMutationBlockReason(provider, workspace)`.
    fn block_reason(&self) -> Option<String> {
        let updates = crate::updates::of(&self.host)?;
        let current = !self.host.closing() && {
            let view = self.host.view();
            view.snapshot
                .workspaces()
                .iter()
                .any(|w| w.id() == self.workspace.id() && w.path() == self.workspace.path() && w.remote() == self.workspace.remote())
        };
        let usage = crate::updates::provider_use(&self.host, &self.provider);
        updates.activity().plugin_mutation_block_reason(&self.provider, current, self.workspace.remote().is_none(), &usage)
    }

    /// C# `OperateAsync`: refused (the reason shown) while something else holds the CLI, or (W6) without `--allow-state-write` for a
    /// real reader; else the operation through the app's operations object, the holds announced before and after, the sign-ins held
    /// for it resent once it ended.
    fn operate(self: &Arc<Self>, install: Option<String>) {
        let refusal = self
            .block_reason()
            .or_else(|| (!self.fake && !self.host.may_change_machine()).then(|| get("windows.notifications.statusVerificationMode")));
        if let Some(reason) = refusal {
            self.browser.show_result(ClaudePluginOperationResult::new(status::SKIPPED, reason));
            self.touch();
            return;
        }
        let Some(updates) = crate::updates::of(&self.host) else { return };
        let panel = self.clone();
        tauri::async_runtime::spawn(async move {
            let host = panel.host.clone();
            let work = {
                let (operations, browser, reader) = (updates.operations.clone(), panel.browser.clone(), panel.reader.clone());
                let token = panel.closing.clone();
                async move {
                    match install {
                        Some(id) => operations.install(&browser, &reader, &id, &token).await,
                        None => operations.refresh_marketplaces(&browser, &reader, &token).await,
                    }
                }
            };
            let running = tauri::async_runtime::spawn(work);
            // The phase was set as the operation began: the window and the composers show it now.
            tokio::task::yield_now().await;
            crate::updates::notify(&host);
            panel.touch();
            let _ = running.await;
            if !host.closing() {
                panel.touch();
                crate::updates::notify(&host);
                crate::accounts::resend_after_update(&host);
            }
        });
    }
}

/// `plugins::open`: C# `OpenPluginBrowser(provider)` — the plugin browser of `provider` (`claude` or `codex`) for the active workspace,
/// a panel dialog, until it closes; nothing while a sheet is up. `openPlugins` and the slash palette call it.
pub async fn open(host: &Arc<Host>, provider: &str) {
    let workspace = {
        let view = host.view();
        if view.dialog_open {
            return;
        }
        view.snapshot.active_workspace_id.as_deref().and_then(|id| view.snapshot.workspace(id)).cloned()
    };
    let Some(workspace) = workspace else { return };
    let _ = crate::frame::act(host, async { show(host, provider, workspace).await.map(|_| ()) }).await;
}

/// C# `ShowPluginBrowser`: the window over `workspace`, read once it shows, until it closes; its browser then (none when another
/// sheet held the window). A remote workspace is refused with C#'s sentence.
pub async fn show(host: &Arc<Host>, provider: &str, workspace: Workspace) -> Result<Option<Arc<ClaudePluginBrowser>>, String> {
    if workspace.remote().is_some() {
        return Err(get("remote.workspace.pluginsOnHost"));
    }
    let updates = crate::updates::of(host).ok_or_else(|| "the run engine is not ready".to_string())?;
    let Some(sheet) = crate::frame::Sheet::begin(host) else { return Ok(None) };
    let browser = Arc::new(ClaudePluginBrowser::new(provider, workspace.clone()));
    let stand_in = with_hooks(|hooks| hooks.reader.clone());
    let fake = stand_in.is_some();
    let reader = match stand_in {
        Some(factory) => factory(provider),
        None => updates.operations.reader(provider).reader(),
    };
    let panel = Arc::new(Panel {
        host: host.clone(),
        provider: provider.to_string(),
        workspace,
        browser: browser.clone(),
        reader: reader.clone(),
        fake,
        id: Mutex::new(None),
        closing: CancellationToken::new(),
    });
    let actions = {
        let (reload, install, refresh) = (panel.clone(), panel.clone(), panel.clone());
        PluginActions {
            reload: Arc::new(move || reload.load()),
            install: Arc::new(move |id| install.operate(Some(id))),
            refresh: Arc::new(move || refresh.operate(None)),
        }
    };

    let (body_browser, body_actions) = (browser.clone(), actions.clone());
    let mut spec = DialogSpec::new(browser.title(), move |ui| Content::El(Box::new(view::plugin_body(ui, &body_browser, &body_actions))))
        .styled(Some(view::PLUGIN_SHEET_WIDTH), Some(view::PLUGIN_SHEET_HEIGHT));
    let subtitle = view::plugin_subtitle(&browser);
    let (tools_browser, tools_actions, status_browser, cancel_browser, busy_browser, close_browser) =
        (browser.clone(), actions.clone(), browser.clone(), browser.clone(), browser.clone(), browser.clone());
    spec.chrome = Some(DialogChrome {
        panel: Some("plugins".into()),
        icon: Some("puzzle".into()),
        subtitle: Some(Box::new(move || Some(subtitle.clone()))),
        danger: false,
        min_width: Some(view::PLUGIN_SHEET_MIN_WIDTH),
        min_height: Some(view::PLUGIN_SHEET_MIN_HEIGHT),
        tools: Some(Box::new(move |ui| view::plugin_tools(ui, &tools_browser, &tools_actions))),
        status: Some(Box::new(move |ui| view::plugin_status(ui, &status_browser))),
        actions: Some(Box::new(move |ui| view::plugin_actions(ui, &cancel_browser))),
        busy: Some(Box::new(move || !busy_browser.can_close())),
    });
    // The Mac keeps 닫기 disabled while an operation runs; Esc and the ✕ are refused the same way.
    spec.can_close = Some(Box::new(move || close_browser.can_close()));

    let handle = crate::native::start_dialog(host, spec);
    *panel.id.lock().unwrap_or_else(|p| p.into_inner()) = Some(handle.id.clone());
    with_hooks(|hooks| hooks.open = Some((browser.clone(), handle.id.clone())));
    // The dialog's Opened: the first read.
    panel.load();
    let id = handle.id.clone();
    handle.wait().await;
    // Closing stops admitting reads; there is nothing to roll back.
    panel.closing.cancel();
    reader.shutdown();
    with_hooks(|hooks| {
        if hooks.open.as_ref().is_some_and(|(_, open)| *open == id) {
            hooks.open = None;
        }
    });
    drop(sheet);
    Ok(Some(browser))
}

/// `plugins::operations_running`: C# `pluginOperations.IsRunning` — a plugin install, refresh or automatic update is under way.
#[allow(dead_code, reason = "a seam W6-B and W6-D calls")]
pub fn operations_running(host: &Host) -> bool {
    crate::updates::of(host).is_some_and(|updates| updates.operations.is_running())
}

#[cfg(test)]
mod tests {
    /// The Rust half of `WindowIsARealWinUISurfaceWiredIntoTheApp` (C#: the run pane menu and the palette open the window): the
    /// page's pane button calls `openPlugins`, which the host answers with this window (methods.rs registers it with C#'s guard).
    #[test]
    fn the_page_reaches_the_plugin_window_through_open_plugins() {
        assert!(crate::methods::registry().names().contains("openPlugins"));
    }
}
