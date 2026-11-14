//! `tools`: 구성 요소 and 내 작업 도구 모음 (C# MainWindow.Settings.cs:253-571, MainWindow.Setup.cs:175-384). Owner: W6-B.
//!
//! - 구성 요소 is the setup screen's explanation over its list (setup.rs keeps the screen's state; drawing the list counts as it shown,
//!   which starts its probes and poll).
//! - 내 작업 도구 모음 is the toolkit's last read: the store's list (`toolkit.json` in the profile folder with `--allow-state-write`, else
//!   this machine's local folder for the profile: mighty-tools `store_folder`), each tool's approval and file-only probe (a smoke's
//!   probe reads its own folder only), the last run's results. The box reads it on first showing and after each action, on a task of
//!   its own; the builder reads only what was read.
//! - Add and import read a JSON file the user picks, export writes one; approve and remove change the store; each failure is said over
//!   the tools with its reason. The install lists the plan in one confirmation (D7) and runs it through `ProcessToolkitExecutor` only
//!   when this run may change the machine; a smoke's install returns at once (C#: the smoke drives the runner itself).

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};

use mighty_native::surface::DialogResult;
use mighty_native::ui::{El, Ui};
use mighty_platform::Paths;
use mighty_run::strings;
use mighty_tools::toolkit::{
    self, ProbeResult, ProcessToolkitExecutor, ToolkitEntrySource, ToolkitProbeContext, ToolkitRunner, ToolkitStore,
};
use mighty_tools::view::{self, ToolkitActions, ToolkitModel, ToolkitRow};
use tauri::Manager;

use crate::host::Host;

/// The toolkit box's state, managed by the app.
pub struct Toolkit {
    /// The store, made on first use in its folder (or the smoke's fixture folder).
    store: Mutex<Option<Arc<ToolkitStore>>>,
    /// What the box shows.
    pub model: Mutex<ToolkitModel>,
    /// The box was read once.
    read: AtomicBool,
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

pub fn toolkit_of(host: &Host) -> Arc<Toolkit> {
    if let Some(toolkit) = host.app.try_state::<Arc<Toolkit>>() {
        return toolkit.inner().clone();
    }
    host.app.manage(Arc::new(Toolkit {
        store: Mutex::new(None),
        model: Mutex::new(ToolkitModel::default()),
        read: AtomicBool::new(false),
    }));
    host.app.state::<Arc<Toolkit>>().inner().clone()
}

/// `toolkit.json`'s folder: the profile's with `--allow-state-write`, else this machine's local folder for the profile (the app's
/// folders as main.rs resolved and manages them).
fn store_folder(host: &Host) -> PathBuf {
    match host.app.try_state::<Arc<dyn Paths>>() {
        Some(paths) => toolkit::store_folder(paths.inner().as_ref(), host.writes()),
        None if host.writes() => host.data_dir.clone(),
        // No local folder known: never the profile's without the flag.
        None => std::env::temp_dir().join("dev.mightycode.tauri").join("toolkit"),
    }
}

impl Toolkit {
    /// The store (C# `GetToolkitStore`).
    pub fn store(&self, host: &Host) -> Arc<ToolkitStore> {
        lock(&self.store).get_or_insert_with(|| Arc::new(ToolkitStore::new(store_folder(host)))).clone()
    }

    /// The smoke's own store in place of the app's (None: the app's again, made anew).
    pub fn replace_store(&self, store: Option<Arc<ToolkitStore>>) {
        *lock(&self.store) = store;
        *lock(&self.model) = ToolkitModel::default();
        self.read.store(false, Ordering::SeqCst);
    }
}

/// The probe's view of this PC (C# `LiveProbeContext`), or a smoke's: its store's folder only.
pub fn probe_context(host: &Host, store: &ToolkitStore) -> ToolkitProbeContext {
    if host.smoke.is_some() {
        return ToolkitProbeContext::smoke(store.directory());
    }
    let local = host.app.path().local_data_dir().unwrap_or_default();
    let separator = if cfg!(windows) { ';' } else { ':' };
    ToolkitProbeContext::live(mighty_history::history::user_home(), &std::env::var("PATH").unwrap_or_default(), local, separator)
}

/// C# `FillToolkitList`'s reading: the store's list with each tool's approval and probe, and what the file says when it failed.
pub fn read(host: &Host) {
    let toolkit = toolkit_of(host);
    toolkit.read.store(true, Ordering::SeqCst);
    let store = toolkit.store(host);
    let (entries, error) = store.list();
    let context = probe_context(host, &store);
    let rows = entries
        .into_iter()
        .map(|entry| {
            let approval = (entry.source != ToolkitEntrySource::Bundled).then(|| store.get_approval(&entry)).flatten();
            let installed = toolkit::probe::probe(&entry, approval.as_ref(), &context) == ProbeResult::Installed;
            ToolkitRow { entry, approved: approval.is_some(), installed }
        })
        .collect();
    {
        let mut model = lock(&toolkit.model);
        model.rows = rows;
        model.error = error;
    }
    crate::native::touch(host, crate::settings::SURFACE);
}

/// 구성 요소 (Components) (C# `BuildComponentsSection`): the setup screen's explanation over its list.
pub fn components(host: &Arc<Host>, ui: &mut Ui) -> El {
    let list = crate::setup::list(host, ui);
    crate::setup::components_drawn(host);
    view::components_box(ui, list)
}

/// 내 작업 도구 모음 (Toolkit) (C# `BuildToolkitSection`): the box over the last read; the first showing reads it.
pub fn toolkit(host: &Arc<Host>, ui: &mut Ui) -> El {
    let toolkit = toolkit_of(host);
    if !toolkit.read.swap(true, Ordering::SeqCst) {
        let reader = host.clone();
        tauri::async_runtime::spawn_blocking(move || read(&reader));
    }
    let model = lock(&toolkit.model).clone();
    view::toolkit_box(ui, &model, Arc::new(Actions(host.clone())))
}

/// C# `ToolkitFileAction`: nothing while an install runs; a failure said over the tools; what worked read again (`reload`).
async fn file_action(host: &Arc<Host>, reload: bool, action: impl std::future::Future<Output = Result<(), String>>) {
    let toolkit = toolkit_of(host);
    if lock(&toolkit.model).running {
        return;
    }
    if let Err(error) = action.await {
        lock(&toolkit.model).error = Some(error);
        crate::native::touch(host, crate::settings::SURFACE);
        return;
    }
    if reload && !host.closing() {
        let reader = host.clone();
        let _ = tauri::async_runtime::spawn_blocking(move || read(&reader)).await;
    }
}

/// A JSON file the user picks (C#'s `FileOpenPicker` with `.json`), owned by the app's window; None when none was picked.
async fn pick_json(host: &Host) -> Option<PathBuf> {
    use tauri_plugin_dialog::DialogExt;
    let (sender, receiver) = tokio::sync::oneshot::channel();
    let mut dialog = host.app.dialog().file().add_filter("JSON", &["json"]);
    if let Some(window) = host.app.get_window(crate::host::WINDOW) {
        dialog = dialog.set_parent(&window);
    }
    dialog.pick_file(move |file| {
        let _ = sender.send(file);
    });
    receiver.await.ok().flatten().and_then(|file| file.into_path().ok())
}

/// Where the export goes (C#'s `FileSavePicker`, `toolkit.json`).
async fn pick_export(host: &Host) -> Option<PathBuf> {
    use tauri_plugin_dialog::DialogExt;
    let (sender, receiver) = tokio::sync::oneshot::channel();
    let mut dialog = host.app.dialog().file().set_file_name("toolkit").add_filter("JSON", &["json"]);
    if let Some(window) = host.app.get_window(crate::host::WINDOW) {
        dialog = dialog.set_parent(&window);
    }
    dialog.save_file(move |path| {
        let _ = sender.send(path);
    });
    receiver.await.ok().flatten().and_then(|path| path.into_path().ok())
}

/// A picked file's text as C#'s `FileIO.ReadTextAsync` reads it: its byte-order mark left out, UTF-16 by its mark.
fn read_text(path: &Path) -> Result<String, String> {
    std::fs::read(path).map(|bytes| toolkit::file::decode_text(&bytes)).map_err(|error| error.to_string())
}

/// C# `AddToolkitEntry`: the one entry a JSON file holds (a file that is no entry is said over the tools).
async fn add(host: Arc<Host>) -> Result<(), String> {
    let Some(path) = pick_json(&host).await else { return Ok(()) };
    let name = path.file_name().map(|name| name.to_string_lossy().into_owned()).unwrap_or_default();
    let entry = toolkit::file::parse_added_entry(&read_text(&path)?)
        .ok_or_else(|| strings::get_with("settings.toolkit.errorEntryFileTemplate", &[("name", &name)]))?;
    toolkit_of(&host).store(&host).add(&entry)
}

/// C# `ExportToolkit`: every entry, the other OS's too, without approvals.
async fn export(host: Arc<Host>) -> Result<(), String> {
    let Some(path) = pick_export(&host).await else { return Ok(()) };
    let text = toolkit_of(&host).store(&host).export();
    std::fs::write(&path, text).map_err(|error| error.to_string())
}

/// C# `ImportToolkit`: an exported list merged in.
async fn import(host: Arc<Host>) -> Result<(), String> {
    let Some(path) = pick_json(&host).await else { return Ok(()) };
    toolkit_of(&host).store(&host).import(&read_text(&path)?)
}

/// C# `RunToolkitInstall`: nothing while one runs, nothing in a smoke; the plan (nothing when empty) in one confirmation (D7), then
/// run with every button waiting, its results shown and the list read again. Without `--allow-state-write` it says so.
async fn install(host: Arc<Host>) -> Result<(), String> {
    let toolkit = toolkit_of(&host);
    if lock(&toolkit.model).running || host.smoke.is_some() {
        return Ok(());
    }
    if !host.may_change_machine() {
        // Said on a line of its own, before any confirmation (the verification mode's words, as the accounts and updates say them).
        host.log.line("toolkit: the install was refused (no --allow-state-write)");
        lock(&toolkit.model).notice = Some(crate::setup::verification_mode());
        crate::native::touch(&host, crate::settings::SURFACE);
        return Ok(());
    }
    let Some(runs) = host.runs().cloned() else { return Ok(()) };
    let store = toolkit.store(&host);
    let context = probe_context(&host, &store);
    // The plan probes the disk: off the async workers. A skill that names no folder fails it, said in the banner (C#'s `Act`).
    let (planned, planner_context) = (store.clone(), context.clone());
    let plan = tauri::async_runtime::spawn_blocking(move || ToolkitRunner::new(&planned, planner_context).plan())
        .await
        .map_err(|error| error.to_string())??;
    if plan.is_empty() {
        return Ok(());
    }
    let confirm = view::toolkit_confirmation(toolkit::runner::presentation::plan_rows(&plan));
    if crate::native::show_dialog(&host, confirm).await != DialogResult::Primary {
        return Ok(());
    }
    let running = Running::start(&host, &toolkit);
    let executor = ProcessToolkitExecutor {
        processes: host.processes.clone(),
        environment: runs.catalog.environment(),
        // C# `CliRunner`: the program along the PATH (× PATHEXT) only.
        resolve: Arc::new(|name, environment| toolkit::runner::find_on_path(name, environment, &|path| Path::new(path).is_file())),
        working_directory: std::env::current_dir().unwrap_or_default(),
    };
    let results = tauri::async_runtime::spawn_blocking(move || {
        tauri::async_runtime::block_on(ToolkitRunner::new(&store, context).run(&plan, &executor))
    })
    .await
    .map_err(|error| error.to_string())?;
    lock(&toolkit.model).results = Some(results);
    drop(running);
    let reader = host.clone();
    let _ = tauri::async_runtime::spawn_blocking(move || read(&reader)).await;
    Ok(())
}

/// The box's buttons waiting while an install runs (C# `HoldToolkit`): released when the install ends however it ends (its task
/// failing or dropped included), never left waiting.
struct Running(Arc<Toolkit>, Arc<Host>);

impl Running {
    fn start(host: &Arc<Host>, toolkit: &Arc<Toolkit>) -> Running {
        lock(&toolkit.model).running = true;
        crate::native::touch(host, crate::settings::SURFACE);
        Running(toolkit.clone(), host.clone())
    }
}

impl Drop for Running {
    fn drop(&mut self) {
        lock(&self.0.model).running = false;
        crate::native::touch(&self.1, crate::settings::SURFACE);
    }
}

/// What the box's buttons do.
struct Actions(Arc<Host>);

impl Actions {
    /// A picker never opens in a smoke (no one answers it).
    fn picks(&self, what: &str) -> bool {
        if self.0.smoke.is_some() {
            self.0.log.line(&format!("smoke: the toolkit's {what} opens no picker"));
            return false;
        }
        true
    }
}

impl ToolkitActions for Actions {
    fn add(&self) {
        if self.picks("add") {
            let host = self.0.clone();
            crate::native::act(&self.0, async move {
                file_action(&host, true, add(host.clone())).await;
                Ok(())
            });
        }
    }

    fn export(&self) {
        if self.picks("export") {
            let host = self.0.clone();
            crate::native::act(&self.0, async move {
                file_action(&host, false, export(host.clone())).await;
                Ok(())
            });
        }
    }

    fn import(&self) {
        if self.picks("import") {
            let host = self.0.clone();
            crate::native::act(&self.0, async move {
                file_action(&host, true, import(host.clone())).await;
                Ok(())
            });
        }
    }

    fn install(&self) {
        crate::native::act(&self.0, install(self.0.clone()));
    }

    fn approve(&self, id: &str) {
        let (host, id) = (self.0.clone(), id.to_string());
        crate::native::act(&self.0, async move {
            let store = toolkit_of(&host).store(&host);
            file_action(&host, true, async move { store.approve(&id, None) }).await;
            Ok(())
        });
    }

    fn remove(&self, id: &str) {
        let (host, id) = (self.0.clone(), id.to_string());
        crate::native::act(&self.0, async move {
            let store = toolkit_of(&host).store(&host);
            file_action(&host, true, async move { store.remove(&id) }).await;
            Ok(())
        });
    }
}
