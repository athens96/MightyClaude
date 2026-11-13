//! The files pane, the reference bubble, HTML frames and the picture viewer's actions on the host's side (C#
//! MainWindow.WebShell.Panes.cs's files part, MainWindow.WebShell.cs's `referencePreview`, `referenceChanges`, `referenceOpen`,
//! `referenceReveal`, `referenceSize`, `htmlFrame`, `imageData` and `imageAction`, MainWindow.Files.cs's `OpenFilePane`,
//! MainWindow.TranscriptImages.cs). Every rule is mighty-files' (the C# Core classes); the OS is reached through mighty-platform's
//! files ports ([`FilePorts`]: an open file's handle, a PDF's first page, show in Explorer, open with the default app, the
//! clipboard) and the save dialog (Tauri's dialog plugin).
//!
//! - Files: each workspace's tree (`FilePaneTree`) lives while the app runs, so a closed and reopened pane shows what it showed;
//!   folders and previews are read off the page's thread. `openFiles` opens the workspace's one files pane left of the current pane.
//! - HTML frames: an HTML file's folder is served under a host of its own (`h<random>.html.mighty.invalid`, at most eight) through
//!   the `mightyhtml` custom protocol; the page's frame is sandboxed to scripts and its policy allows only these hosts.
//! - File originals: a Claude run's Edit, Write and MultiEdit results are kept by a [`FileOriginalStore`] (the profile's
//!   `file-originals` with `--allow-state-write`, else this machine's local folder for the profile), pruned at the start to the panes
//!   and runs the state names, a pane's originals dropped when it closes and a run's when its graph lets it go.

use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use mighty_composer::images as pictures;
use mighty_composer::svg;
use mighty_core::AppSnapshot;
use mighty_core::layout;
use mighty_core::locale::Locale;
use mighty_core::model::{AgentImageRef, PaneLayoutNode, RunSession};
use mighty_core::shell::{effective_layout, layout_mode};
use mighty_core::wire;
use mighty_files::changes::{self, FileOriginalStore};
use mighty_files::frames::{self, FrameHosts};
use mighty_files::listing::{self, ListError, Listing};
use mighty_files::model::{self, Formats, Plain};
use mighty_files::patch::FileEdit;
use mighty_files::tree::FilePaneTree;
use mighty_files::{pane, preview, raster, reference};
use mighty_graph::view::MightyPane;
use mighty_platform::{BoxFuture, ClipboardImage, FilePorts, SaveDialog, SaveRequest};
use serde_json::{Value, json};
use tauri::{AppHandle, Manager};

use crate::host::{Host, Registry, View, lock};
use crate::methods::{flag, number, text};

/// The custom protocol HTML frames are served on (`http://mightyhtml.<host>/…` on Windows, `mightyhtml://<host>/…` on the Mac).
pub const PROTOCOL: &str = "mightyhtml";
/// C#'s `{ Length: > 0 and <= 4096 }` on a path argument (UTF-16 units).
const MAXIMUM_PATH_UNITS: usize = 4096;
/// C# `WebPictureBytes`, `WebSvgEdge`: the most a picture handed to the page may weigh, and the longest edge an SVG is drawn at.
const WEB_PICTURE_BYTES: usize = 8 * 1024 * 1024;
const WEB_SVG_EDGE: u32 = 2048;

/// The files pane's state for the app's life.
pub struct Files {
    /// Each workspace's tree (opened folders, selection, the file last previewed).
    trees: Mutex<HashMap<String, FilePaneTree>>,
    frames: Mutex<FrameHosts>,
    pub originals: Arc<FileOriginalStore>,
    pub ports: FilePorts,
    save: Arc<dyn SaveDialog>,
    formats: Arc<dyn Formats>,
    /// Each pane's kept runs as last seen (a pane gone takes its originals; a run its graph let go takes its own).
    kept: Mutex<HashMap<String, HashSet<String>>>,
    /// The paths the frames protocol served last (the smoke's check), at most 256.
    pub served: Mutex<Vec<String>>,
}

/// The user's number and date formats, from the OS where it says (C# `CultureInfo.CurrentCulture`).
struct HostFormats;

impl Formats for HostFormats {
    fn n0(&self, value: i64) -> String {
        mighty_run::strings::n0(value)
    }
    fn decimal(&self) -> String {
        crate::platform::decimal_separator().unwrap_or_else(|| ".".into())
    }
    fn date_time(&self, at: std::time::SystemTime) -> String {
        crate::platform::short_date_time(at).unwrap_or_else(|| Plain.date_time(at))
    }
}

/// The OS's save dialog through Tauri's dialog plugin, owned by the app's window (C# `FileSavePicker` initialized with it).
struct TauriSave(AppHandle);

impl SaveDialog for TauriSave {
    fn pick(&self, request: SaveRequest) -> BoxFuture<'_, mighty_platform::Result<Option<PathBuf>>> {
        let app = self.0.clone();
        Box::pin(async move {
            use tauri_plugin_dialog::DialogExt;
            let (sender, receiver) = tokio::sync::oneshot::channel();
            let mut dialog =
                app.dialog().file().set_file_name(&request.suggested_name).add_filter(&request.filter_name, &[&request.extension]);
            if let Some(window) = app.get_window(crate::host::WINDOW) {
                dialog = dialog.set_parent(&window);
            }
            dialog.save_file(move |path| {
                let _ = sender.send(path);
            });
            Ok(receiver.await.ok().flatten().and_then(|path| path.into_path().ok()))
        })
    }
}

/// The files pane's state, made once the host exists (before the run engine, which keeps the originals through it).
pub fn start(host: &Arc<Host>, originals_dir: PathBuf) {
    let ports = crate::platform::file_ports();
    // Every reader of the user's files (pictures, HTML assets, the files pane) asks the open handle where it is.
    mighty_composer::files::use_file_handles(ports.handles.clone());
    let originals = Arc::new(FileOriginalStore::new(originals_dir, changes::MAXIMUM_BYTES, ports.handles.clone()));
    let snapshot = host.view().snapshot.clone();
    let kept = FileOriginalStore::kept_runs_of(snapshot.sessions().iter().map(|s| s.as_ref()));
    let files = Arc::new(Files {
        trees: Mutex::new(HashMap::new()),
        frames: Mutex::new(FrameHosts::default()),
        originals: originals.clone(),
        ports,
        save: Arc::new(TauriSave(host.app.clone())),
        formats: Arc::new(HostFormats),
        kept: Mutex::new(kept.clone()),
        served: Mutex::new(Vec::new()),
    });
    host.app.manage(files.clone());
    // C# DesktopService.InitializeAsync: what the saved state no longer names goes.
    std::thread::spawn(move || originals.prune(&kept));
    let watcher = host.clone();
    std::thread::Builder::new()
        .name("file-originals".into())
        .spawn(move || {
            loop {
                std::thread::sleep(Duration::from_secs(2));
                let sessions = watcher.view().snapshot.sessions().to_vec();
                retain_originals(&files, &sessions);
            }
        })
        .expect("the file originals' watch");
}

/// C# `DesktopService`: a closed pane's originals go (`Forget`), and a run its graph let go takes its own (`Retain`).
fn retain_originals(files: &Files, sessions: &[Arc<RunSession>]) {
    let now = FileOriginalStore::kept_runs_of(sessions.iter().map(|s| s.as_ref()));
    let mut kept = lock(&files.kept);
    for (pane, runs) in kept.iter() {
        match now.get(pane) {
            None => files.originals.forget(pane),
            Some(current) if runs.iter().any(|r| !current.contains(r)) => files.originals.retain(pane, current),
            Some(_) => {}
        }
    }
    *kept = now;
}

/// The run engine's hook (mighty-run `FileOriginals`): an Edit, Write or MultiEdit result of a Claude run, kept as C#
/// `FileOriginalStore.Capture` keeps it (the parser's `EditedOriginal` reading of the result: `FileEdit::from_result`).
pub fn originals_hook(app: &AppHandle) -> Option<mighty_run::manager::FileOriginals> {
    let store = app.try_state::<Arc<Files>>()?.originals.clone();
    Some(Arc::new(move |session: &str, run: &str, root: &str, path: &str, tool: &str, result: &Value| {
        store.capture(session, run, root, path, &FileEdit::from_result(tool, result));
    }))
}

fn state(host: &Host) -> Result<Arc<Files>, String> {
    host.app.try_state::<Arc<Files>>().map(|s| s.inner().clone()).ok_or_else(|| "the files pane is not ready".to_string())
}

/// A path argument C# takes: present, 1–4,096 UTF-16 units.
fn path_arg(args: &Value) -> Option<String> {
    text(args, "path").filter(|p| (1..=MAXIMUM_PATH_UNITS).contains(&p.encode_utf16().count())).map(str::to_string)
}

/// C# `LocalPath(workspace)`.
fn local_path(state: &AppSnapshot, workspace_id: &str) -> Option<String> {
    state.workspace(workspace_id).filter(|w| w.remote.is_none()).map(|w| w.path().to_string())
}

/// C# `WebFiles(args)`: the files pane's workspace and folder; None for a remote workspace (its folder is on its host); "Session not
/// found." for any other pane.
fn files_target(host: &Host, args: &Value) -> Result<Option<(String, String)>, String> {
    let view = host.view();
    let pane = text(args, "paneId")
        .and_then(|id| view.snapshot.session(id))
        .filter(|s| pane::is_file_pane(Some(s.kind())))
        .ok_or_else(|| "Session not found.".to_string())?;
    let workspace = pane.workspace_id().to_string();
    Ok(local_path(&view.snapshot, &workspace).map(|root| (workspace, root)))
}

/// C# `RequireWebFiles`.
fn require_files(host: &Host, args: &Value) -> Result<(String, String), String> {
    files_target(host, args)?.ok_or_else(|| host.locale.get("remote.windows.workspace.paneUnavailable"))
}

/// C# `WebLoadFolder`: one folder's listing, read off the page's thread, applied to the workspace's tree.
async fn load_folder(files: &Files, locale: Locale, workspace: &str, root: &str, path: &str) {
    let (root, folder) = (PathBuf::from(root), path.to_string());
    let listing: Result<Listing, ListError> =
        tauri::async_runtime::spawn_blocking(move || listing::list(&folder, &root, listing::MAXIMUM_ENUMERATED_NAMES))
            .await
            .unwrap_or(Err(ListError::Unreadable));
    lock(&files.trees).entry(workspace.to_string()).or_default().apply(path, listing, &locale);
}

fn files_model(files: &Files, locale: &Locale, workspace: &str) -> Value {
    let trees = lock(&files.trees);
    let empty = FilePaneTree::default();
    model::files_model(trees.get(workspace).unwrap_or(&empty), locale, files.formats.as_ref())
}

fn with_tree<T>(files: &Files, workspace: &str, change: impl FnOnce(&mut FilePaneTree) -> T) -> T {
    change(lock(&files.trees).entry(workspace.to_string()).or_default())
}

/// C# `WebFilePreview`: the file read (`FilePreviewLoader`) and drawn for the page, off its thread.
async fn file_preview(files: Arc<Files>, locale: Locale, root: String, path: String) -> Value {
    let worker = path.clone();
    tauri::async_runtime::spawn_blocking(move || {
        let data = preview::load(Path::new(&root), &worker, files.ports.handles.as_ref(), &locale);
        model::file_preview(&worker, &data, &locale, files.formats.as_ref(), files.ports.pdf.as_ref())
    })
    .await
    .unwrap_or_else(|_| model::failed_preview(&path, "", &locale.get("files.preview.failed")))
}

/// C# `WebImageUrl`: a picture's bytes as a data URL the page's policy allows: PNG, JPEG, GIF and WebP as they are, an SVG drawn to
/// PNG no longer than 2,048 (NativeSvgRaster); None for any other kind, more than 8 MB, or an SVG that does not draw.
pub fn web_image_url(data: Option<Vec<u8>>, media_type: &str) -> Option<String> {
    let mut data = data.filter(|d| d.len() <= WEB_PICTURE_BYTES)?;
    let mut kind = media_type.to_string();
    if kind == "image/svg+xml" || svg::svg_document(&data) {
        data = raster::svg_png(&data, WEB_SVG_EDGE, false).ok()?.png;
        kind = "image/png".into();
    }
    use base64::Engine as _;
    (data.len() <= WEB_PICTURE_BYTES && matches!(kind.as_str(), "image/png" | "image/jpeg" | "image/gif" | "image/webp"))
        .then(|| format!("data:{kind};base64,{}", base64::engine::general_purpose::STANDARD.encode(&data)))
}

/// C# `WebReferenceRoot`: the folder the pane's runs work in (a delegated child's worktree folder, `DelegationPanes.RunWorkspace`);
/// None for a remote pane (its files are on its host) or an unknown one.
fn reference_root(view: &View, args: &Value) -> Option<String> {
    let pane = text(args, "paneId").and_then(|id| view.snapshot.session(id))?;
    let workspace = view.snapshot.workspace(pane.workspace_id()).filter(|w| w.remote.is_none())?;
    Some(pane.working_folder.clone().unwrap_or_else(|| workspace.path().to_string()))
}

/// C# `DesktopService.OriginalsRun`: a graph run's `sourceRunID` (else its id); an id no graph run of the pane has stays as it is.
fn originals_run(view: &View, pane: &str, run: &str) -> String {
    view.snapshot
        .session(pane)
        .and_then(|p| p.graph_runs().iter().find(|r| r.id() == run).map(|r| r.source_run_id.clone().unwrap_or_else(|| r.id().to_string())))
        .unwrap_or_else(|| run.to_string())
}

/// C# `WorkspaceFiles.Resolve(path, root)` read off the page's thread.
async fn resolve(path: String, root: String) -> Option<PathBuf> {
    tauri::async_runtime::spawn_blocking(move || mighty_core::workspace_files::resolve(&path, Path::new(&root))).await.ok().flatten()
}

/// The error banner (C# `error.Text = ex.Message`).
fn banner(host: &Host, message: String) {
    host.view().error = Some(message);
}

// ---- the files pane in the layout (C# OpenFilePane, OpenSlimPane) -------------------------------------------------------------

fn save_layout(state: &mut AppSnapshot, workspace: &str, node: Option<PaneLayoutNode>) {
    let layouts = state.pane_layouts.get_or_insert_with(Default::default);
    match node {
        Some(node) => {
            layouts.insert(workspace.to_string(), Some(node));
        }
        None => {
            layouts.shift_remove(workspace);
        }
    }
}

fn save_selection(state: &mut AppSnapshot, workspace: &str, id: &str) {
    state.pane_layout_active_session_ids.get_or_insert_with(Default::default).insert(workspace.to_string(), Some(id.to_string()));
}

/// C# `SelectLayoutSession`: the pane's group shows it, and it becomes the active pane of its workspace, which becomes the active one.
fn select_session(view: &mut View, id: &str) -> Result<(), String> {
    view.shows_dashboard = false;
    let Some(workspace) = view.snapshot.session(id).map(|s| s.workspace_id().to_string()) else { return Ok(()) };
    let node = effective_layout(&view.snapshot, &workspace, &mut view.layout_defaults).map(|n| layout::select(&n, id));
    view.update(|state| {
        save_layout(state, &workspace, node);
        save_selection(state, &workspace, id);
        state.active_workspace_id = Some(workspace.clone());
        state.active_session_id = Some(id.to_string());
    })
}

/// C# `OpenFilePane` (`OpenSlimPane`): the workspace's one files pane shown: focused when open, otherwise put left of the current
/// pane (or as a tab when there is no room for a split). A refusal is the error banner's words (C# `Act`).
pub fn open_files_pane(view: &mut View, locale: &Locale, workspace_id: Option<&str>) -> Result<(), String> {
    let state = view.snapshot.clone();
    let id = workspace_id.map(str::to_string).or_else(|| state.active_workspace_id.clone()).unwrap_or_default();
    let workspace = state.workspace(&id).ok_or_else(|| locale.get("window.error.addWorkspaceFirst"))?;
    // A remote workspace's folder is on its host: no files pane reads it here.
    if workspace.remote.is_some() {
        return Err(locale.get("remote.windows.workspace.paneUnavailable"));
    }
    let workspace = workspace.id().to_string();
    let pane_id = pane::pane_id(&workspace);
    if state.session(&pane_id).is_some() {
        return select_session(view, &pane_id);
    }
    if !pane::has_room(state.sessions().len()) {
        return Err(locale.get("files.error.noRoom"));
    }
    let current = if state.active_workspace_id.as_deref() == Some(workspace.as_str()) {
        state.active_session_id.clone()
    } else {
        state.pane_layout_active_session_ids.as_ref().and_then(|m| m.get(&workspace)).cloned().flatten()
    };
    let pane = RunSession {
        id: Some(pane_id.clone()),
        workspace_id: Some(workspace.clone()),
        kind: Some(pane::KIND.into()),
        title: Some(locale.get("files.pane.title")),
        ..RunSession::default()
    };
    // The pane joins the tree before it becomes the active one, as a new pane does (AddToLayout).
    let mut joined = state.clone();
    joined.sessions_mut().push(Arc::new(pane.clone()));
    let tree = effective_layout(&joined, &workspace, &mut view.layout_defaults);
    let placed = tree.map(|tree| layout::select(&pane::place(&tree, &pane_id, current.as_deref()), &pane_id));
    let focus = layout_mode(&joined, &workspace) == "focus";
    view.update(|state| {
        state.sessions_mut().push(Arc::new(pane));
        state.active_workspace_id = Some(workspace.clone());
        state.active_session_id = Some(pane_id.clone());
        if let Some(placed) = placed {
            let mode = if placed.kind() == "split" { "custom" } else { "tabs" };
            save_layout(state, &workspace, Some(placed));
            if !focus {
                state.pane_layout_modes.get_or_insert_with(Default::default).insert(workspace.clone(), Some(mode.to_string()));
            }
        }
        save_selection(state, &workspace, &pane_id);
    })
}

// ---- the picture viewer's actions (C# PaneView.LoadTranscriptImage, TranscriptImageAction) ------------------------------------

/// A picture as the viewer holds it: the verified bytes, their kind, and what is drawn (an SVG rendered to PNG).
struct ShownImage {
    bytes: Vec<u8>,
    media_type: String,
    display: Vec<u8>,
}

/// C# `TranscriptAction("image", value, image)`.
struct PictureAction {
    value: String,
    image: Option<AgentImageRef>,
}

/// C# `LoadTranscriptImage`: read and checked the way the viewer opens it (stored, a file under the workspace or a temporary root,
/// or inline); an SVG is drawn to PNG no larger than 4,096.
fn load_picture(
    action: &PictureAction,
    root: Option<&str>,
    cache: &pictures::AgentImageCache,
    locale: &Locale,
) -> Result<ShownImage, String> {
    let missing = || locale.get("images.missing");
    let bytes = match &action.image {
        Some(image) => cache.data(image),
        None => {
            let temporary = pictures::temporary_roots();
            match pictures::locate(&action.value, root.map(Path::new), Some(&temporary)) {
                pictures::AgentImageLocation::File { path, root } => pictures::read_located(&path, &root).ok(),
                pictures::AgentImageLocation::Inline { base64, .. } => pictures::decode_base64(&base64).ok(),
                _ => None,
            }
        }
    }
    .ok_or_else(missing)?;
    let declared = action
        .image
        .as_ref()
        .and_then(|i| i.media_type.clone())
        .or_else(|| pictures::media_type_for_file_name(&action.value).map(str::to_string))
        .unwrap_or_else(|| if svg::svg_document(&bytes) { "image/svg+xml".into() } else { "image/png".into() });
    let (media_type, _, _) = pictures::inspect(&bytes, &declared).map_err(|e| pictures::failure_text(e, locale))?;
    let display = if media_type == "image/svg+xml" {
        raster::svg_png(&bytes, 4096, false).map_err(|e| pictures::failure_text(e, locale))?.png
    } else {
        bytes.clone()
    };
    Ok(ShownImage { bytes, media_type: media_type.to_string(), display })
}

/// C# `ImageExternalPath`: the original file (when revealing one the workspace holds), else a verified copy in the picture cache.
fn external_path(
    action: &PictureAction,
    root: Option<&str>,
    (snapshot, media_type): (&[u8], &str),
    reveal_original: bool,
    cache: &pictures::AgentImageCache,
    handles: &dyn mighty_platform::FileHandles,
    locale: &Locale,
) -> Result<PathBuf, String> {
    let missing = || locale.get("images.missing");
    if reveal_original {
        let named = action.image.as_ref().and_then(|i| i.path.clone()).unwrap_or_else(|| action.value.clone());
        let temporary = pictures::temporary_roots();
        if let pictures::AgentImageLocation::File { path, root } = pictures::locate(&named, root.map(Path::new), Some(&temporary)) {
            let relative = mighty_core::workspace_files::relative(&path, &root).ok_or_else(missing)?.replace('\\', "/");
            return mighty_files::open_file(&relative, &root, handles).map(|opened| opened.path).map_err(|_| missing());
        }
    }
    let source = action.image.as_ref().and_then(|i| i.source.clone()).unwrap_or_else(|| "image".into());
    let image = cache.store(snapshot, media_type, &source, None).map_err(|_| missing())?;
    let path = cache.path_for(&image).ok_or_else(missing)?;
    let cache_root = mighty_core::workspace_files::real_path(cache.directory()).ok_or_else(missing)?;
    let relative = mighty_core::workspace_files::relative(&path, &cache_root).ok_or_else(missing)?.replace('\\', "/");
    let opened = mighty_files::open_file(&relative, &cache_root, handles).map_err(|_| missing())?;
    let mut data = Vec::new();
    use std::io::Read as _;
    opened.file.take(pictures::MAXIMUM_IMAGE_BYTES as u64 + 1).read_to_end(&mut data).map_err(|_| missing())?;
    if opened.size != snapshot.len() as u64 || pictures::sha256(&data) != image.hash.unwrap_or_default() {
        return Err(missing());
    }
    Ok(opened.path)
}

/// C# `TranscriptImageAction`: `copy` puts the drawn picture on the clipboard, `save` writes the original through the save dialog,
/// `reveal` selects the original file (else a verified cache copy) in Explorer, `openDefault` hands the default app a verified
/// snapshot (an SVG as its PNG, so a browser association never runs the agent's document).
async fn picture_action(
    host: &Arc<Host>,
    files: &Arc<Files>,
    action: PictureAction,
    root: Option<String>,
    name: &str,
) -> Result<(), String> {
    let cache = host.engine()?.images.clone();
    let locale = host.locale;
    let (worker_files, worker_cache, worker_root) = (files.clone(), cache.clone(), root.clone());
    let shown = tauri::async_runtime::spawn_blocking(move || {
        load_picture(&action, worker_root.as_deref(), &worker_cache, &locale).map(|s| (s, action))
    })
    .await
    .map_err(|e| e.to_string())??;
    let (shown, action) = shown;
    match name {
        "copy" => tauri::async_runtime::spawn_blocking(move || {
            let (width, height, rgba, png) = raster::clipboard_pixels(&shown.display).ok_or_else(|| locale.get("images.missing"))?;
            worker_files.ports.desktop.copy_image(&ClipboardImage { width, height, rgba, png }).map_err(|e| e.to_string())
        })
        .await
        .map_err(|e| e.to_string())?,
        "save" => {
            let extension = pictures::EXTENSIONS.iter().find(|(t, _)| *t == shown.media_type).map_or("png", |(_, e)| *e);
            let request =
                SaveRequest { suggested_name: "image".into(), filter_name: shown.media_type.clone(), extension: extension.into() };
            if let Some(path) = files.save.pick(request).await.map_err(|e| e.to_string())? {
                std::fs::write(&path, &shown.bytes).map_err(|e| e.to_string())?;
            }
            Ok(())
        }
        "reveal" | "openDefault" => {
            let reveal = name == "reveal";
            tauri::async_runtime::spawn_blocking(move || {
                let (snapshot, kind) = if reveal {
                    (&shown.bytes, shown.media_type.as_str())
                } else {
                    (&shown.display, if shown.media_type == "image/svg+xml" { "image/png" } else { shown.media_type.as_str() })
                };
                let path = external_path(
                    &action,
                    root.as_deref(),
                    (snapshot, kind),
                    reveal,
                    &cache,
                    worker_files.ports.handles.as_ref(),
                    &locale,
                )?;
                if reveal {
                    worker_files.ports.desktop.reveal(&path).map_err(|e| e.to_string())
                } else {
                    worker_files.ports.desktop.open(&path).map(|_| ()).map_err(|e| e.to_string())
                }
            })
            .await
            .map_err(|e| e.to_string())?
        }
        _ => Ok(()),
    }
}

// ---- the HTML frames' protocol ----------------------------------------------------------------------------------------------

/// Where a frame host's files start: the protocol as the OS's webview reaches it.
fn frame_origin(host: &str) -> String {
    if cfg!(windows) { format!("http://{PROTOCOL}.{host}") } else { format!("{PROTOCOL}://{host}") }
}

/// C# `WebHtmlAddress`: the file's address under its folder's host, or None.
fn html_address(files: &Files, file: &Path) -> Option<String> {
    lock(&files.frames).address(file, frame_origin)
}

/// The `mightyhtml` protocol: a frame's request answered from the folder its host serves (C#'s virtual host mapping, DenyCors), 404 for
/// anything else. Only the app's own window asks.
pub fn protocol(
    context: tauri::UriSchemeContext<'_, tauri::Wry>,
    request: tauri::http::Request<Vec<u8>>,
    responder: tauri::UriSchemeResponder,
) {
    let app = context.app_handle().clone();
    let from_main = context.webview_label() == crate::host::WINDOW;
    tauri::async_runtime::spawn_blocking(move || {
        let not_found =
            || tauri::http::Response::builder().status(404).header("Cache-Control", "no-store").body(Vec::new()).unwrap_or_default();
        let Some(files) = app.try_state::<Arc<Files>>().map(|s| s.inner().clone()) else { return responder.respond(not_found()) };
        let host = request.uri().host().unwrap_or("").to_string();
        let folder = from_main.then(|| lock(&files.frames).folder(&host)).flatten();
        let served = folder.and_then(|folder| frames::serve(&folder, request.method().as_str(), request.uri().path()));
        match served {
            Some(served) => {
                let mut log = lock(&files.served);
                if log.len() == 256 {
                    log.remove(0);
                }
                log.push(request.uri().path().to_string());
                drop(log);
                let response = tauri::http::Response::builder()
                    .status(200)
                    .header("Content-Type", served.media_type)
                    .header("X-Content-Type-Options", "nosniff")
                    .header("Cache-Control", "no-store")
                    .body(served.bytes)
                    .unwrap_or_default();
                responder.respond(response);
            }
            None => responder.respond(not_found()),
        }
    });
}

// ---- the page's methods -----------------------------------------------------------------------------------------------------

pub fn register(methods: &mut Registry) {
    methods.add("openFiles", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        if !view.dialog_open {
            view.shows_dashboard = false;
            if let Err(message) = open_files_pane(&mut view, &host.locale, text(&args, "workspaceId")) {
                view.error = Some(message);
            }
        }
        Ok(Value::Null)
    });
    methods.add("filesState", |host: Arc<Host>, args| async move {
        let Some((workspace, root)) = files_target(&host, &args)? else { return Ok(model::unavailable()) };
        let files = state(&host)?;
        let (loaded, wanted) = with_tree(&files, &workspace, |tree| {
            let mut wanted: Vec<String> = tree.expanded.iter().filter(|p| !tree.children.contains_key(*p)).cloned().collect();
            wanted.sort();
            (tree.children.contains_key(""), wanted)
        });
        if !loaded {
            load_folder(&files, host.locale, &workspace, &root, "").await;
        }
        for path in wanted {
            load_folder(&files, host.locale, &workspace, &root, &path).await;
        }
        Ok(files_model(&files, &host.locale, &workspace))
    });
    methods.add("filesToggle", |host: Arc<Host>, args| async move {
        let (workspace, root) = require_files(&host, &args)?;
        let files = state(&host)?;
        let path = text(&args, "path").unwrap_or("").to_string();
        // Opening a folder always reads it again; the earlier listing shows while it reads.
        let opened = with_tree(&files, &workspace, |tree| {
            tree.selected_path = Some(path.clone());
            if tree.expanded.remove(&path) {
                false
            } else {
                tree.expanded.insert(path.clone());
                true
            }
        });
        if opened {
            load_folder(&files, host.locale, &workspace, &root, &path).await;
        }
        Ok(files_model(&files, &host.locale, &workspace))
    });
    methods.add("filesReveal", |host: Arc<Host>, args| async move {
        let (workspace, root) = require_files(&host, &args)?;
        let files = state(&host)?;
        let path = text(&args, "path").unwrap_or("").to_string();
        let mut folders = with_tree(&files, &workspace, |tree| {
            tree.reveal(&path);
            tree.expanded.iter().filter(|p| path == **p || path.starts_with(&format!("{p}/"))).cloned().collect::<Vec<_>>()
        });
        folders.sort_by_key(|p| p.len());
        for folder in folders {
            load_folder(&files, host.locale, &workspace, &root, &folder).await;
        }
        Ok(files_model(&files, &host.locale, &workspace))
    });
    methods.add("filesFilter", |host: Arc<Host>, args| async move {
        let (workspace, _) = require_files(&host, &args)?;
        let files = state(&host)?;
        with_tree(&files, &workspace, |tree| tree.filter = text(&args, "text").unwrap_or("").to_string());
        Ok(files_model(&files, &host.locale, &workspace))
    });
    methods.add("filesSelect", |host: Arc<Host>, args| async move {
        let (workspace, _) = require_files(&host, &args)?;
        let files = state(&host)?;
        with_tree(&files, &workspace, |tree| tree.selected_path = text(&args, "path").map(str::to_string));
        Ok(files_model(&files, &host.locale, &workspace))
    });
    methods.add("filesRefresh", |host: Arc<Host>, args| async move {
        let (workspace, root) = require_files(&host, &args)?;
        let files = state(&host)?;
        let targets = with_tree(&files, &workspace, FilePaneTree::refresh_targets);
        let loads = targets.iter().map(|path| load_folder(&files, host.locale, &workspace, &root, path));
        futures_join(loads).await;
        Ok(files_model(&files, &host.locale, &workspace))
    });
    methods.add("filesPreview", |host: Arc<Host>, args| async move {
        let (workspace, root) = require_files(&host, &args)?;
        let files = state(&host)?;
        let path = text(&args, "path").unwrap_or("").to_string();
        with_tree(&files, &workspace, |tree| tree.preview_path = Some(path.clone()));
        Ok(file_preview(files, host.locale, root, path).await)
    });
    methods.add("filesShowInExplorer", |host: Arc<Host>, args| async move {
        let (_, root) = require_files(&host, &args)?;
        let files = state(&host)?;
        if let Some(path) = resolve(text(&args, "path").unwrap_or("").to_string(), root).await
            && let Err(error) = files.ports.desktop.reveal(&path)
        {
            banner(&host, error.to_string());
        }
        Ok(Value::Null)
    });
    // Its default app, as Windows' launcher opens it (refusing programs), only for a file that still resolves inside the workspace.
    methods.add("filesOpen", |host: Arc<Host>, args| async move {
        let (_, root) = require_files(&host, &args)?;
        let files = state(&host)?;
        let Some(path) = resolve(text(&args, "path").unwrap_or("").to_string(), root).await.filter(|p| !p.is_dir()) else {
            return Ok(json!(false));
        };
        Ok(json!(files.ports.desktop.open(&path).unwrap_or(false)))
    });
    methods.add("referencePreview", |host: Arc<Host>, args| async move {
        let Some(root) = reference_root(&host.view(), &args) else { return Ok(Value::Null) };
        let Some(path) = path_arg(&args) else { return Ok(Value::Null) };
        let line = number(&args, "line").filter(|n| n.fract() == 0.0 && *n > 0.0 && *n <= i32::MAX as f64).map(|n| n as i32);
        let resolving = root.clone();
        let target = tauri::async_runtime::spawn_blocking(move || reference::resolve(&path, &resolving, line)).await.ok().flatten();
        let Some(target) = target else { return Ok(json!({"found": false})) };
        let files = state(&host)?;
        let preview = file_preview(files, host.locale, root, target.relative_path.clone()).await;
        let mut answer = json!({"found": true, "relativePath": target.relative_path});
        if let Some(line) = target.line {
            answer["line"] = json!(line);
        }
        answer["preview"] = preview;
        Ok(answer)
    });
    // The same file's 변경사항: against the original the pane's run (runId, else its newest run that captured one) kept, else git's
    // HEAD; null when there is no diff to show.
    methods.add("referenceChanges", |host: Arc<Host>, args| async move {
        let (root, pane, run) = {
            let view = host.view();
            let Some(root) = reference_root(&view, &args) else { return Ok(Value::Null) };
            let Some(pane) = text(&args, "paneId").map(str::to_string) else { return Ok(Value::Null) };
            let run = text(&args, "runId").filter(|r| wire::identifier(r)).map(|r| originals_run(&view, &pane, r));
            (root, pane, run)
        };
        let Some(path) = path_arg(&args) else { return Ok(Value::Null) };
        let resolving = root.clone();
        let Some(target) = tauri::async_runtime::spawn_blocking(move || reference::resolve(&path, &resolving, None)).await.ok().flatten()
        else {
            return Ok(Value::Null);
        };
        let files = state(&host)?;
        let changes = changes::read(
            Some(&files.originals),
            host.processes.as_ref(),
            files.ports.handles.as_ref(),
            &root,
            &target.relative_path,
            Some(&pane),
            run.as_deref(),
        )
        .await;
        Ok(changes.map_or(Value::Null, |c| c.to_json()))
    });
    // Resolved again at the click: the workspace or the file may have changed while the bubble was open.
    methods.add("referenceOpen", |host: Arc<Host>, args| async move {
        let Some(root) = reference_root(&host.view(), &args) else { return Ok(json!(false)) };
        let files = state(&host)?;
        let Some(path) = resolve(text(&args, "path").unwrap_or("").to_string(), root).await.filter(|p| !p.is_dir()) else {
            return Ok(json!(false));
        };
        Ok(json!(files.ports.desktop.open(&path).unwrap_or(false)))
    });
    methods.add("referenceReveal", |host: Arc<Host>, args| async move {
        let Some(root) = reference_root(&host.view(), &args) else { return Ok(Value::Null) };
        let files = state(&host)?;
        if let Some(path) = resolve(text(&args, "path").unwrap_or("").to_string(), root).await
            && let Err(error) = files.ports.desktop.reveal(&path)
        {
            banner(&host, error.to_string());
        }
        Ok(Value::Null)
    });
    // The size a drag left, every pane's (none: 420 wide and as tall as the room), kept as ReferencePreview.BubbleSize keeps it.
    methods.add("referenceSize", |host: Arc<Host>, args| async move {
        let (width, height) = reference::bubble_size(number(&args, "width"), number(&args, "height"));
        host.view().update(|state| {
            state.reference_bubble_width = width;
            state.reference_bubble_height = height;
        })?;
        Ok(Value::Null)
    });
    // An HTML file as a page: its folder served under a name of its own for the sandboxed frame the page draws, so its css, scripts
    // and pictures beside it load. `reference` says the file is a reference bubble's (the pane's run folder), not a files pane's.
    methods.add("htmlFrame", |host: Arc<Host>, args| async move {
        let root =
            if flag(&args, "reference") { reference_root(&host.view(), &args) } else { files_target(&host, &args)?.map(|(_, root)| root) };
        let (Some(root), Some(path)) = (root, path_arg(&args)) else { return Ok(Value::Null) };
        let Some(file) = resolve(path, root).await else { return Ok(Value::Null) };
        let html = file.extension().map(|e| e.to_string_lossy().to_ascii_lowercase()).is_some_and(|e| e == "html" || e == "htm");
        if file.is_dir() || !html {
            return Ok(Value::Null);
        }
        let files = state(&host)?;
        Ok(html_address(&files, &file).map_or(Value::Null, Value::from))
    });
    // The picture viewer's buttons (src/pictures/PictureViewer.tsx): the picture named as its thumbnail or tile was fetched, read and
    // checked again and handed to the viewer's own actions. Answers null when done, else the failure's words.
    methods.add("imageAction", |host: Arc<Host>, args| async move {
        let Some(name) = text(&args, "action").filter(|a| matches!(*a, "copy" | "save" | "reveal" | "openDefault")).map(str::to_string)
        else {
            return Ok(Value::Null);
        };
        let Some(pane_id) = text(&args, "paneId").map(str::to_string) else { return Ok(Value::Null) };
        let found = {
            let mut view = host.view();
            match crate::panes::ensure(&mut view, Some(&pane_id)) {
                Err(error) => Err(error),
                Ok(_) => {
                    let pane = view.snapshot.session(&pane_id).cloned().ok_or_else(|| "Session not found.".to_string())?;
                    let root = local_path(&view.snapshot, pane.workspace_id());
                    let action = match text(&args, "hash") {
                        Some(hash) => pane
                            .logs()
                            .iter()
                            .flat_map(|e| e.images.iter().flatten().flatten())
                            .find(|i| i.hash.as_deref() == Some(hash))
                            .map(|image| PictureAction { value: image.source.clone().unwrap_or_default(), image: Some(image.clone()) }),
                        None => gallery_image(&pane, root.as_deref(), &args),
                    };
                    Ok((action, root))
                }
            }
        };
        let (action, root) = match found {
            Ok(found) => found,
            Err(error) => return Ok(json!(error)),
        };
        let Some(action) = action else { return Ok(json!(host.locale.get("images.missing"))) };
        let files = state(&host)?;
        match picture_action(&host, &files, action, root, &name).await {
            Ok(()) => Ok(Value::Null),
            Err(error) => Ok(json!(error)),
        }
    });
}

/// C# `WebGalleryImage`: a diagram tile's picture, its item named by run, step and id.
fn gallery_image(pane: &RunSession, root: Option<&str>, args: &Value) -> Option<PictureAction> {
    let locator = crate::mighty::locate;
    let page = MightyPane::images(pane, &[], text(args, "runId").unwrap_or(""), text(args, "step").unwrap_or(""), true, root, &locator)?;
    let id = text(args, "id").unwrap_or("");
    page.shown.into_iter().find(|item| item.id == id).map(|item| PictureAction { value: item.source, image: item.stored })
}

/// Every load, one after another (C# `Task.WhenAll` reads them at once; the tree ends the same).
async fn futures_join<F: std::future::Future<Output = ()>>(loads: impl Iterator<Item = F>) {
    for load in loads {
        load.await;
    }
}
