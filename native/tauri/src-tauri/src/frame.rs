//! The frame's remaining methods (C# MainWindow.WebShell.Shell.cs `RegisterShellMethods`, MainWindow.Navigation.cs, MainWindow.cs
//! `PickFolder`/`AddPane`, MainWindow.ResumeSession.cs, MainWindow.SmallParity.cs, MainWindow.TextAppearance.cs): closing, focusing,
//! renaming and removing, the add-pane menu and what its entries do, a new pane, the folder picker, Explorer, the text size, and the
//! page-drawn dialogs' flows (`dialogs.rs`). Every action runs as C#'s `Act`: the error banner cleared first, what it fails with shown
//! there (the call itself answers null).
//!
//! What another W4 module owns comes in through a seam main.rs sets once at start: the files pane (`OPEN_FILES`: files.rs's
//! `open_files`, C# `OpenFilePane`) and the folder's earlier sessions (`RESUME`: history.rs's `HistoryResume` over mighty-history, C#
//! `ResumableSessions`; the list's rows are history.rs's `resumeRows`, which reads [`open_resume`]). Until one is set, the files entry
//! does nothing and an agent pane starts new at once (nothing to continue).

use std::future::Future;
use std::path::Path;
use std::pin::Pin;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, OnceLock};

use mighty_core::add_pane::{self, inherit, save_layout, save_mode, save_selection};
use mighty_core::layout;
use mighty_core::model::{RunSession, Workspace};
use mighty_core::normalize::provider_name;
use mighty_core::rename::{self, strings};
use mighty_core::shell::{effective_layout, layout_mode, open_disclosure};
use mighty_core::text_appearance::{self, Stored, TextAppearance};
use mighty_core::{AppSnapshot, model_label};
use mighty_history::{ResumableSession, resumable};
use mighty_platform::{PrefValue, PrefsStore};
use serde_json::{Value, json};
use tauri::Manager;

use crate::dialogs::{self, without_nulls};
use crate::host::{Host, Registry, Reply, View};
use crate::methods::{flag, number, text};

pub type Pending = Pin<Box<dyn Future<Output = Result<(), String>> + Send>>;
/// C# `OpenFilePane(workspaceId)`: the files pane's module opens it (the add menu's files entry).
pub type OpenFiles = fn(Arc<Host>, Option<String>) -> Pending;
pub static OPEN_FILES: OnceLock<OpenFiles> = OnceLock::new();

/// The folder's earlier sessions (C# `ResumableSessions`, `DesktopService.RememberSession`): history.rs's `HistoryResume`.
pub trait ResumeSource: Send + Sync {
    /// `AddAgentPane`'s look-up: the head of the newest record (`HeadOnly`, `MaximumSessions = 1`) of `provider`'s sessions the
    /// workspace's folder recorded, nested runs hidden. Called off the async workers; it may take long.
    fn head(&self, host: &Arc<Host>, provider: &str, workspace: &Workspace) -> Vec<ResumableSession>;
    /// The list sheet opens ([`open_resume`] names its agent and folder): its rows are read afresh.
    fn open(&self, host: &Arc<Host>);
    /// The list sheet closed with the id of the row picked (none when it was cancelled): that session (from the listing its
    /// `resumeRows` read last), remembered as the app's own (`RememberSession`: never hidden as an automated run), or the words why not
    /// (another pane took it while the list was open: never two panes on one session); none when the list showed no such session.
    fn picked(&self, host: &Arc<Host>, id: Option<&str>) -> Option<Result<ResumableSession, String>>;
}

pub static RESUME: OnceLock<Arc<dyn ResumeSource>> = OnceLock::new();

/// The resume list the page shows (C# `webResume`): its agent and workspace.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OpenResume {
    pub provider: String,
    pub workspace_id: String,
    pub path: String,
}

/// The window's state the frame's flows keep (C# fields of `MainWindow`).
#[derive(Default)]
struct Frame {
    /// The text setting now (`AppText.Current`), read from the settings store the first time.
    text: Mutex<Option<TextAppearance>>,
    /// The resume list open now.
    resume: Mutex<Option<OpenResume>>,
    /// `resumeLookup`: one look-up at a time.
    looking_up: AtomicBool,
}

fn frame(host: &Host) -> Arc<Frame> {
    if let Some(frame) = host.app.try_state::<Arc<Frame>>() {
        return frame.inner().clone();
    }
    host.app.manage(Arc::new(Frame::default()));
    host.app.state::<Arc<Frame>>().inner().clone()
}

/// The resume list the page shows now, for `resumeRows` (C# `webResume`).
pub fn open_resume(host: &Host) -> Option<OpenResume> {
    lock(&frame(host).resume).clone()
}

fn lock<T>(mutex: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// C# `Act`: the banner cleared, the action run, what it failed with shown in the banner; the call answers null.
pub async fn act(host: &Arc<Host>, action: impl Future<Output = Result<(), String>>) -> Reply {
    host.view().error = None;
    if let Err(error) = action.await {
        host.view().error = Some(error);
    }
    Ok(Value::Null)
}

/// `dialogOpen = true` for as long as a flow's sheet is up; none when one is up already (one sheet at a time).
pub struct Sheet(Arc<Host>);

impl Sheet {
    pub fn begin(host: &Arc<Host>) -> Option<Sheet> {
        let mut view = host.view();
        if view.dialog_open {
            return None;
        }
        view.dialog_open = true;
        Some(Sheet(host.clone()))
    }
}

impl Drop for Sheet {
    fn drop(&mut self) {
        self.0.view().dialog_open = false;
        self.0.schedule();
    }
}

/// A sheet's answer: what the page answered, or null when the host closed it or the app ends.
async fn answer_of(receiver: tokio::sync::oneshot::Receiver<Value>) -> Value {
    receiver.await.unwrap_or(Value::Null)
}

// ── the window's layout actions (C# MainWindow.Layout.cs; methods.rs, terminal.rs, files.rs and surface.rs use them too) ──

/// C# `SelectLayoutSession`: the pane's group shows it, and it becomes the active pane of its workspace, which becomes the active one.
pub fn select_session(view: &mut View, id: &str) -> Result<(), String> {
    view.shows_dashboard = false;
    let Some(workspace) = view.snapshot.session(id).map(|s| s.workspace_id().to_string()) else { return view.update(|_| {}) };
    let node = effective_layout(&view.snapshot, &workspace, &mut view.layout_defaults).map(|n| layout::select(&n, id));
    view.update(|state| {
        save_layout(state, &workspace, node);
        save_selection(state, &workspace, id);
        state.active_workspace_id = Some(workspace.clone());
        state.active_session_id = Some(id.to_string());
    })
}

/// C# `ApplyLayoutPreset`.
pub fn apply_layout_preset(view: &mut View, preset: &str) -> Result<(), String> {
    let Some(workspace) = view.snapshot.active_workspace_id.clone() else {
        return view.update(|state| state.layout = Some(preset.to_string()));
    };
    let node = if matches!(preset, "custom" | "focus") {
        effective_layout(&view.snapshot, &workspace, &mut view.layout_defaults)
    } else {
        layout::preset(&view.snapshot.pane_ids(&workspace), preset, view.snapshot.active_session_id.as_deref())
    };
    view.update(|state| {
        save_layout(state, &workspace, node);
        save_mode(state, &workspace, preset);
    })
}

/// C# `ActivatePane`: a pane of the active workspace becomes the active one (a press inside it, a browser view taking the keyboard).
pub fn activate_pane(view: &mut View, id: &str) -> Result<(), String> {
    if view.shows_dashboard || view.snapshot.active_session_id.as_deref() == Some(id) {
        return Ok(());
    }
    let workspace = view.snapshot.session(id).map(|s| s.workspace_id().to_string());
    let active = view.snapshot.active_workspace_id.clone();
    view.update(|state| {
        if let Some(workspace) = workspace.filter(|w| Some(w) == active.as_ref()) {
            save_selection(state, &workspace, id);
            state.active_session_id = Some(id.to_string());
        }
    })
}

/// C# `SelectWorkspace`: opened in the sidebar and made active, with the pane it last showed (else its first group's).
pub fn select_workspace(view: &mut View, id: &str) -> Result<(), String> {
    if view.snapshot.workspace(id).is_none() {
        return Ok(());
    }
    view.shows_dashboard = false;
    let preferred = view.snapshot.pane_layout_active_session_ids.as_ref().and_then(|m| m.get(id)).cloned().flatten();
    let selected = match view.snapshot.sessions().iter().find(|s| s.workspace_id() == id && Some(s.id()) == preferred.as_deref()) {
        Some(s) => Some(s.id().to_string()),
        None => effective_layout(&view.snapshot, id, &mut view.layout_defaults)
            .and_then(|tree| layout::groups(&tree).first().and_then(|g| g.selected_session_id.clone())),
    };
    let expanded = open_disclosure(&view.snapshot, id);
    view.update(|state| {
        state.expanded_workspace_ids = expanded;
        state.active_workspace_id = Some(id.to_string());
        state.active_session_id = selected;
    })
}

// ── panes ──

/// C# `AddPane`: a shell or agent pane (`kind` "shell", "browser", "claude") in the active workspace, in `group` or the active
/// pane's, made the active one; a 129th pane is refused with the Mac's sentence. True when it was added.
fn add_pane(
    host: &Arc<Host>,
    kind: &str,
    provider: &str,
    group: Option<&str>,
    shape: impl FnOnce(RunSession) -> RunSession,
) -> Result<bool, String> {
    let locale = host.locale;
    let mut view = host.view();
    let workspace = view.snapshot.active_workspace_id.clone().ok_or_else(|| locale.get("window.error.addWorkspaceFirst"))?;
    let remote = view.snapshot.workspace(&workspace).is_some_and(|w| w.remote.is_some());
    if !matches!(kind, "claude" | "shell") && remote {
        return Err(locale.get("remote.windows.workspace.paneUnavailable"));
    }
    let title = if kind == "shell" {
        locale.get(if remote { "remote.pane.commandTitle" } else { "session.title.shell" })
    } else {
        provider_name(provider).to_string()
    };
    let pane = RunSession {
        workspace_id: Some(workspace.clone()),
        kind: Some(kind.into()),
        provider: Some(provider.into()),
        title: Some(title),
        ..RunSession::default()
    };
    let pane = shape(inherit(pane, view.snapshot.sessions()));
    if !add_pane::has_room(&view.snapshot) {
        return Err(locale.get("workspace.paneLimit"));
    }
    let mut defaults = std::mem::take(&mut view.layout_defaults);
    let result = view.update(|state| *state = add_pane::add_to_layout(state, &workspace, pane, group, &mut defaults));
    view.layout_defaults = defaults;
    view.shows_dashboard = false;
    result.map(|()| true)
}

/// C# `AddBrowserPane`: a browser tab of the active workspace, titled as one, its web profile the workspace's.
fn add_browser_pane(host: &Arc<Host>, group: Option<&str>) -> Result<(), String> {
    let remote = {
        let view = host.view();
        view.snapshot.active_workspace_id.as_deref().and_then(|id| view.snapshot.workspace(id)).is_some_and(|w| w.remote.is_some())
    };
    if remote {
        return Err(host.locale.get("remote.windows.workspace.paneUnavailable"));
    }
    let title = host.locale.get("browser.tab.title");
    add_pane(host, "browser", "claude", group, |pane| RunSession {
        title: Some(title),
        workspace_profile_key: pane.workspace_id.clone(),
        ..pane
    })
    .map(|_| ())
}

/// C# `WebAddAgentPane` (and `AddAgentPane`): an agent pane from the menu; for an agent whose sessions can be continued, the folder's
/// newest record is looked up first (off the workers), and with one no other pane continues, Start new / Resume… is asked (in its
/// checking state when the look-up outlasts `QuietLookUp`, Start new working at once).
async fn add_agent_pane(host: &Arc<Host>, provider: &str, group: Option<String>) -> Result<(), String> {
    let workspace = {
        let view = host.view();
        view.snapshot.active_workspace_id.as_deref().and_then(|id| view.snapshot.workspace(id)).cloned()
    };
    let source = RESUME.get().cloned();
    let (Some(workspace), Some(source)) = (workspace.filter(|w| w.remote.is_none()), source) else {
        add_pane(host, "claude", provider, group.as_deref(), |pane| pane)?;
        return Ok(());
    };
    if !resumable::may_ask_resume(provider, true) {
        add_pane(host, "claude", provider, group.as_deref(), |pane| pane)?;
        return Ok(());
    }
    let state = frame(host);
    if host.view().dialog_open || state.looking_up.swap(true, Ordering::SeqCst) {
        return Ok(());
    }
    let picked = ask_resume(host, provider, &workspace, source.clone()).await;
    state.looking_up.store(false, Ordering::SeqCst);
    match picked.as_deref() {
        Some("new") => {
            add_pane(host, "claude", provider, group.as_deref(), |pane| pane)?;
        }
        Some("resume") => pick_resumed_session(host, provider, &workspace, group.as_deref(), source).await?,
        _ => {}
    }
    Ok(())
}

/// The look-up and the choice sheet: "new", "resume", or none (cancelled, or the workspace changed meanwhile).
async fn ask_resume(host: &Arc<Host>, provider: &str, workspace: &Workspace, source: Arc<dyn ResumeSource>) -> Option<String> {
    let (reader, owner, wanted, folder) = (source.clone(), host.clone(), provider.to_string(), workspace.clone());
    let mut reading = tauri::async_runtime::spawn_blocking(move || reader.head(&owner, &wanted, &folder));
    let asks = |host: &Arc<Host>, found: &[ResumableSession]| {
        let in_use = resumable::in_use(host.view().snapshot.sessions().iter().map(|s| s.as_ref()));
        resumable::asks_resume(provider, found, &in_use)
    };
    // The smoke answers every sheet itself, so it always waits for the look-up.
    let early = if host.smoke.is_some() {
        Some((&mut reading).await)
    } else {
        tokio::time::timeout(std::time::Duration::from_millis(resumable::QUIET_LOOK_UP_MS), &mut reading).await.ok()
    };
    if host.view().snapshot.active_workspace_id.as_deref() != Some(workspace.id()) {
        return None;
    }
    let quiet = early.is_some();
    let early = early.map(|found| found.unwrap_or_default());
    if let Some(found) = &early
        && !asks(host, found)
    {
        return Some("new".into());
    }
    let _sheet = Sheet::begin(host)?;
    let agent = model_label::provider_label(provider);
    let dialogs = dialogs::of(host);
    let (id, answer) = dialogs.open(json!({
        "type": "resumeChoice", "provider": provider, "checking": !quiet,
        "title": host.locale.get_with("resume.choice.title", &[("provider", agent)]),
        "message": host.locale.get_with("resume.choice.message", &[("provider", agent)]),
    }));
    let mut answer = answer;
    if !quiet {
        tokio::select! {
            picked = &mut answer => return picked.ok().and_then(|v| v.as_str().map(str::to_string)),
            found = &mut reading => {
                // Nothing to continue after all: the pane is added as Start new adds it.
                if !asks(host, &found.unwrap_or_default()) {
                    dialogs.close(id);
                    return Some("new".into());
                }
                dialogs.update(id, false);
            }
        }
    }
    answer_of(answer).await.as_str().map(str::to_string)
}

/// C# `WebPickResumedSession`: the list (its rows are `resumeRows`'), the pick checked against the panes open now and remembered, then
/// a pane that continues it (`ResumableSessions.Apply`: titled by the session until it sends a request of its own).
async fn pick_resumed_session(
    host: &Arc<Host>,
    provider: &str,
    workspace: &Workspace,
    group: Option<&str>,
    source: Arc<dyn ResumeSource>,
) -> Result<(), String> {
    let frame = frame(host);
    let result = {
        let Some(_sheet) = Sheet::begin(host) else { return Ok(()) };
        *lock(&frame.resume) =
            Some(OpenResume { provider: provider.into(), workspace_id: workspace.id().into(), path: workspace.path().into() });
        source.open(host);
        let (_, answer) = dialogs::of(host).open(json!({
            "type": "resumeList", "provider": provider, "title": host.locale.get("resume.title"), "path": workspace.path(),
        }));
        answer_of(answer).await
    };
    let picked = source.picked(host, result.as_str());
    *lock(&frame.resume) = None;
    let Some(session) = picked.transpose()? else { return Ok(()) };
    add_pane(host, "claude", &session.provider.clone(), group, |pane| resumable::apply(&session, &pane))?;
    Ok(())
}

/// C# `CloseSession` / `ClosePane` / `CloseSessionAsync`: the pane's run stopped and ended (at most ten seconds), then the pane gone.
/// Delegation's parent-close question is W7's: no pane has children yet.
async fn close_session(host: &Arc<Host>, id: &str) -> Result<(), String> {
    if let Some(runs) = host.runs()
        && let Err(error) = runs.service.stop(id).await
        && !matches!(error, mighty_run::RunError::TimedOut(_))
    {
        return Err(error.to_string());
    }
    host.view().update(|state| state.sessions_mut().retain(|pane| pane.id() != id))
}

// ── workspaces ──

/// C# `DesktopService.AddWorkspaceAsync` and `OpenWorkspaceFolder`: the folder listed as a workspace (approved for saving) and selected;
/// one with no pane yet gets a Claude pane at once, as Ctrl+N adds one.
pub fn open_workspace_folder(host: &Arc<Host>, path: &Path) -> Result<(), String> {
    let workspace = host.approve_local(path).map_err(|error| {
        let text = error.to_string();
        if text.starts_with("store.error.") { host.locale.get(&text) } else { text }
    })?;
    let has_panes = {
        let mut view = host.view();
        let id = workspace.id().to_string();
        view.update(|state| {
            if !state.workspaces().iter().any(|w| w.id() == id) {
                state.workspaces_mut().push(workspace.clone());
            }
            if let Some(expanded) = open_disclosure(state, &id) {
                state.expanded_workspace_ids = Some(expanded);
            }
            state.active_workspace_id = Some(id.clone());
        })?;
        view.shows_dashboard = false;
        view.snapshot.sessions().iter().any(|s| s.workspace_id() == id)
    };
    if !has_panes {
        add_pane(host, "claude", add_pane::NEW_PANE_SHORTCUT_PROVIDER, None, |pane| pane)?;
    }
    Ok(())
}

/// The OS's folder picker, owned by the main window (C# `FolderPicker` initialized with the window); none when nothing was picked.
pub async fn pick_folder_path(host: &Arc<Host>) -> Option<std::path::PathBuf> {
    use tauri_plugin_dialog::DialogExt;
    let (sender, receiver) = tokio::sync::oneshot::channel();
    let mut dialog = host.app.dialog().file();
    if let Some(window) = host.app.get_window(crate::host::WINDOW) {
        dialog = dialog.set_parent(&window);
    }
    dialog.pick_folder(move |folder| {
        let _ = sender.send(folder);
    });
    receiver.await.ok().flatten().and_then(|folder| folder.into_path().ok())
}

/// C# `ConfirmRemoveWorkspace`: asked in the page (danger, Cancel the default), then `RemoveWorkspaceAsync`: every pane's run stopped,
/// the workspace and its panes gone, another one active. The sheet stays up until the workspace is gone, so the add menu and the
/// other sheets stay off while its runs stop (nothing is added to a workspace on its way out).
async fn remove_workspace(host: &Arc<Host>, id: &str) -> Result<(), String> {
    let Some(name) = host.view().snapshot.workspace(id).map(|w| w.name().to_string()) else { return Ok(()) };
    let Some(_sheet) = Sheet::begin(host) else { return Ok(()) };
    let confirmed = {
        let l = host.locale;
        let (_, answer) = dialogs::of(host).open(json!({
            "type": "confirm", "title": l.get("workspace.remove.title"), "message": l.get_with("workspace.remove.body", &[("name", &name)]),
            "primary": l.get("workspace.menu.remove"), "close": l.get("settings.run.cancelButton"), "defaultPrimary": false, "destructive": true,
        }));
        answer_of(answer).await == Value::Bool(true)
    };
    if !confirmed || host.view().snapshot.workspace(id).is_none() {
        return Ok(());
    }
    let panes: Vec<String> =
        host.view().snapshot.sessions().iter().filter(|s| s.workspace_id() == id).map(|s| s.id().to_string()).collect();
    if let Some(runs) = host.runs() {
        for pane in panes {
            let _ = runs.service.stop(&pane).await;
        }
    }
    host.view().update(|state| {
        state.workspaces_mut().retain(|w| w.id() != id);
        state.sessions_mut().retain(|s| s.workspace_id() != id);
        if state.active_workspace_id.as_deref() == Some(id) {
            state.active_workspace_id = state.workspaces().first().map(|w| w.id().to_string());
        }
    })
}

/// C# `WebRenameDialog`: the rename sheet in the page, its name checked by `renameCheck`; (automatic, the name to save).
async fn rename_dialog(host: &Arc<Host>, heading: &str, hint: &str, current: &str, offer_automatic: bool) -> (bool, Option<String>) {
    let Some(_sheet) = Sheet::begin(host) else { return (false, None) };
    let l = host.locale;
    let dialog = without_nulls(json!({
        "type": "rename", "title": l.get(heading), "hint": l.get(hint), "current": current, "field": l.get(strings::FIELD_LABEL),
        "save": l.get(strings::BUTTON_SAVE), "cancel": l.get(strings::BUTTON_CANCEL),
        "automatic": offer_automatic.then(|| l.get(strings::AUTOMATIC)),
    }));
    let result = answer_of(dialogs::of(host).open(dialog).1).await;
    let choice = result.get("choice").and_then(Value::as_str);
    if offer_automatic && choice == Some("automatic") {
        return (true, None);
    }
    let name = result.get("name").and_then(Value::as_str).filter(|_| choice == Some("save")).and_then(rename::display_name);
    (false, name)
}

async fn rename_workspace(host: &Arc<Host>, id: &str) -> Result<(), String> {
    let Some(current) = host.view().snapshot.workspace(id).map(|w| w.name().to_string()) else { return Ok(()) };
    let (_, name) = rename_dialog(host, strings::HEADING_WORKSPACE, strings::HINT_WORKSPACE, &current, false).await;
    let Some(name) = name else { return Ok(()) };
    let locale = host.locale;
    renamed(host, |state| rename::rename_workspace(state, id, &name, &locale))
}

/// A rename applied to the state as it is when the change is made (C# `UpdateAsync(s => RenameSupport...(s, ...))`); what it refuses
/// with is the banner's.
fn renamed(host: &Arc<Host>, change: impl FnOnce(&AppSnapshot) -> Result<AppSnapshot, String>) -> Result<(), String> {
    let mut refused = None;
    host.view().update(|state| match change(state) {
        Ok(next) => *state = next,
        Err(error) => refused = Some(error),
    })?;
    refused.map_or(Ok(()), Err)
}

/// The smoke's reload step: a workspace's rename flow started as its menu starts it, left waiting on the page's answer.
pub fn start_rename_workspace(host: &Arc<Host>, id: String) {
    let host = host.clone();
    tauri::async_runtime::spawn(async move {
        let _ = act(&host, rename_workspace(&host, &id)).await;
    });
}

/// The composer's `/rename` (C# `SlashPalette`: `owner.RenameSession(id)`): the pane's rename sheet, its flow left to run on its own.
pub fn start_rename_session(host: &Arc<Host>, id: String) {
    let host = host.clone();
    tauri::async_runtime::spawn(async move {
        let _ = act(&host, rename_session(&host, &id)).await;
    });
}

async fn rename_session(host: &Arc<Host>, id: &str) -> Result<(), String> {
    let Some((current, agent)) = host.view().snapshot.session(id).map(|s| (s.title().to_string(), s.kind() == "claude")) else {
        return Ok(());
    };
    let (automatic, name) = rename_dialog(host, strings::HEADING_SESSION, strings::HINT_SESSION, &current, agent).await;
    let locale = host.locale;
    if automatic {
        renamed(host, |state| rename::set_automatic(state, id, &locale))
    } else if let Some(name) = name {
        renamed(host, |state| rename::rename_session(state, id, &name, &locale))
    } else {
        Ok(())
    }
}

/// C# `ShowWorkspaceInExplorer`: a local workspace's folder (its real path) in Explorer; a remote one's is on its host.
fn show_in_explorer(host: &Arc<Host>, id: &str) -> Result<(), String> {
    let folder = host.view().snapshot.workspace(id).filter(|w| w.remote.is_none()).map(|w| w.path().to_string());
    let Some(path) = folder.and_then(|folder| mighty_core::workspace_files::real_path(Path::new(&folder))).filter(|p| p.is_dir()) else {
        return Ok(());
    };
    crate::platform::show_in_explorer(&path)
}

// ── the add menu ──

/// C# `WebAddMenu`: the entries for a workspace (the active one when none is named), the three ways to add a workspace last, then
/// Connect; off while a sheet is open.
fn add_menu(host: &Host, workspace: Option<&str>) -> Value {
    let view = host.view();
    let state = &view.snapshot;
    let workspace = workspace.or(state.active_workspace_id.as_deref()).and_then(|id| state.workspace(id));
    let remote = workspace.is_some_and(|w| w.remote.is_some());
    let mut entries: Vec<Value> = add_pane::entries_for(workspace)
        .iter()
        .map(|entry| {
            if entry == add_pane::SEPARATOR {
                return json!({"entry": entry, "label": ""});
            }
            without_nulls(json!({
                "entry": entry, "label": add_pane::label(entry, remote, &host.locale),
                "provider": add_pane::agent_provider(entry), "shortcut": add_pane::shortcut(entry),
            }))
        })
        .collect();
    entries.push(json!({"entry": "connect", "label": host.locale.get("remote.menu.connect")}));
    without_nulls(json!({"workspaceId": workspace.map(|w| w.id()), "entries": entries, "disabled": view.dialog_open}))
}

/// What an add-menu entry does in the active workspace (C# `addPaneEntry`'s last branches).
async fn add_entry_here(host: &Arc<Host>, entry: &str, group: Option<String>, workspace: Option<String>) -> Result<(), String> {
    match entry {
        add_pane::SHELL => add_pane(host, "shell", "claude", group.as_deref(), |pane| pane).map(|_| ()),
        add_pane::BROWSER => add_browser_pane(host, group.as_deref()),
        add_pane::FILES => {
            host.view().shows_dashboard = false;
            match OPEN_FILES.get() {
                Some(open) => open(host.clone(), workspace).await,
                None => Ok(()),
            }
        }
        _ => match add_pane::agent_provider(entry) {
            Some(provider) => add_agent_pane(host, provider, group).await,
            None => Ok(()),
        },
    }
}

/// C# `addPaneEntry{entry, groupId?, workspaceId?}`.
async fn add_pane_entry(host: &Arc<Host>, entry: &str, group: Option<String>, workspace: Option<String>) -> Result<(), String> {
    let active = host.view().snapshot.active_workspace_id.clone();
    if entry == "connect" {
        // Settings › Remote connection is W6/W8's: nothing to open yet.
        return Ok(());
    }
    if entry == add_pane::OPEN_PROJECT {
        if !host.view().dialog_open
            && let Some(folder) = pick_folder_path(host).await
        {
            open_workspace_folder(host, &folder)?;
        }
        return Ok(());
    }
    if entry == add_pane::NEW_FOLDER || entry == add_pane::FROM_GIT {
        return crate::workspace_create::show(host, entry).await;
    }
    if let Some(other) = workspace.clone().filter(|w| Some(w) != active.as_ref()) {
        // C# `DashboardAddPane`: the workspace made active first, then the entry there.
        let closed = {
            let view = host.view();
            view.dialog_open || view.snapshot.workspace(&other).is_none()
        };
        if closed {
            return Ok(());
        }
        select_workspace(&mut host.view(), &other)?;
        if host.view().snapshot.active_workspace_id.as_deref() != Some(other.as_str()) {
            return Ok(());
        }
        return add_entry_here(host, entry, None, Some(other)).await;
    }
    add_entry_here(host, entry, group, workspace).await
}

// ── the text size ──

/// The saved text setting (C# `TextAppearanceStore.Load`) from the settings store the host was given, else the standard one.
fn saved_text(host: &Host) -> TextAppearance {
    let Some(prefs) = host.app.try_state::<Arc<dyn PrefsStore>>() else { return TextAppearance::default() };
    let read = |key: &str| prefs.get(key).ok().flatten();
    let text = |key: &str| match read(key) {
        Some(PrefValue::Text(text)) => Some(text),
        _ => None,
    };
    let scale = match read(text_appearance::SCALE_KEY) {
        Some(PrefValue::Text(text)) => Some(Stored::Text(text)),
        Some(PrefValue::Int(number)) => Some(Stored::Number(number)),
        _ => None,
    };
    TextAppearance::from_values(
        scale,
        text(text_appearance::FAMILY_KEY).as_deref(),
        text(text_appearance::TRANSCRIPT_FAMILY_KEY).as_deref(),
        text(text_appearance::CODE_FAMILY_KEY).as_deref(),
    )
}

/// The text setting now (C# `AppText.Current`): the saved one until `textSize` changes it; the terminals' size and code font too.
pub fn text_setting(host: &Host) -> TextAppearance {
    let frame = frame(host);
    let mut text = lock(&frame.text);
    text.get_or_insert_with(|| saved_text(host)).clone()
}

fn push_text(host: &Host, text: &TextAppearance) {
    let view = host.view();
    host.push(&view, "text", text.push().to_string());
}

/// C# `textSize{step}` (`SetTextAppearance`): one size larger or smaller, or back to 100% with the fonts kept; the page follows the
/// `text` push. Saving it is Settings' (W6): the registry is shared with the installed app, and this host writes none of it yet.
fn text_size(host: &Host, step: f64) {
    let current = text_setting(host);
    let next = if step == 0.0 { current.with_scale(1.0) } else { current.stepped(if step > 0.0 { 1 } else { -1 }) };
    if next == current {
        return;
    }
    *lock(&frame(host).text) = Some(next.clone());
    push_text(host, &next);
    // C# `TextAppearanceChanged`: the terminals take their size again.
    crate::terminal::restyle(host);
}

/// What the page's `ready` brings back that a reload lost: its open dialogs (`dialogs.rs`) and, when it is not the standard one, the
/// text setting.
pub fn page_ready(host: &Arc<Host>) {
    dialogs::page_ready(host);
    let text = text_setting(host);
    if !text.is_standard() {
        push_text(host, &text);
    }
}

// ── registration ──

fn id(args: &Value) -> Option<String> {
    text(args, "id").map(str::to_string)
}

pub fn register(methods: &mut Registry) {
    dialogs::register(methods);
    crate::workspace_create::register(methods);
    methods.add("closeSession", |host: Arc<Host>, args| async move {
        match id(&args) {
            Some(id) => act(&host, close_session(&host, &id)).await,
            None => Ok(Value::Null),
        }
    });
    methods.add("focusPane", |host: Arc<Host>, args| async move {
        let Some(id) = id(&args) else { return Ok(Value::Null) };
        let toggle = flag(&args, "toggle");
        act(&host, async {
            let mut view = host.view();
            select_session(&mut view, &id)?;
            // menu.focusPane always focuses; session.menu.focus goes back to custom when the layout is focused already.
            let workspace = view.snapshot.active_workspace_id.clone().unwrap_or_default();
            let preset = if toggle && layout_mode(&view.snapshot, &workspace) == "focus" { "custom" } else { "focus" };
            apply_layout_preset(&mut view, preset)
        })
        .await
    });
    methods.add("textSize", |host: Arc<Host>, args| async move {
        text_size(&host, number(&args, "step").unwrap_or(0.0));
        Ok(Value::Null)
    });
    // The resource banner (C# `resourceBanner`) is not fed yet (shell::Pending): nothing to hide.
    methods.add("dismissResource", |_host: Arc<Host>, _| async move { Ok(Value::Null) });
    methods.add("renameSession", |host: Arc<Host>, args| async move {
        match id(&args) {
            Some(id) => act(&host, rename_session(&host, &id)).await,
            None => Ok(Value::Null),
        }
    });
    methods.add("renameWorkspace", |host: Arc<Host>, args| async move {
        match id(&args) {
            Some(id) => act(&host, rename_workspace(&host, &id)).await,
            None => Ok(Value::Null),
        }
    });
    methods.add("removeWorkspace", |host: Arc<Host>, args| async move {
        match id(&args) {
            Some(id) => act(&host, remove_workspace(&host, &id)).await,
            None => Ok(Value::Null),
        }
    });
    methods.add("showInExplorer", |host: Arc<Host>, args| async move {
        match id(&args) {
            Some(id) => act(&host, async { show_in_explorer(&host, &id) }).await,
            None => Ok(Value::Null),
        }
    });
    methods.add("pickFolder", |host: Arc<Host>, _| async move {
        if host.view().dialog_open {
            return Ok(Value::Null);
        }
        act(&host, async {
            match pick_folder_path(&host).await {
                Some(folder) => open_workspace_folder(&host, &folder),
                None => Ok(()),
            }
        })
        .await
    });
    methods.add("addMenu", |host: Arc<Host>, args| async move { Ok(add_menu(&host, text(&args, "workspaceId"))) });
    methods.add("addPaneEntry", |host: Arc<Host>, args| async move {
        let Some(entry) = text(&args, "entry").map(str::to_string) else { return Ok(Value::Null) };
        let (group, workspace) = (text(&args, "groupId").map(str::to_string), text(&args, "workspaceId").map(str::to_string));
        act(&host, add_pane_entry(&host, &entry, group, workspace)).await
    });
    methods.add("newPane", |host: Arc<Host>, args| async move {
        let closed = {
            let view = host.view();
            view.dialog_open || view.snapshot.active_workspace_id.is_none()
        };
        if closed {
            return Ok(Value::Null);
        }
        let (kind, provider) =
            if text(&args, "kind") == Some("shell") { ("shell", "claude") } else { ("claude", add_pane::NEW_PANE_SHORTCUT_PROVIDER) };
        act(&host, async { add_pane(&host, kind, provider, None, |pane| pane).map(|_| ()) }).await
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_text_size_steps_and_resets() {
        let current = TextAppearance::new(1.2, Some("Fira Code"), None, None);
        assert_eq!(current.stepped(1).scale, 1.3);
        assert_eq!(current.with_scale(1.0), TextAppearance::new(1.0, Some("Fira Code"), None, None), "Ctrl+0 keeps the fonts");
    }

    #[test]
    fn a_new_snapshot_has_no_workspace_to_add_to() {
        let state = AppSnapshot::default();
        assert!(state.active_workspace_id.is_none() && add_pane::has_room(&state));
    }
}
