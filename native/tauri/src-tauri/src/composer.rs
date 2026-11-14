//! The composer's page methods (C# MainWindow.WebShell.Composer.cs `RegisterComposerMethods`) over the pane views of panes.rs, each the
//! never-shown `PaneView`'s own rule: the draft (`WebSetDraft`), the send (`Submit`: a busy pane queues, an idle one starts its request
//! through the run service, a start that did not happen puts the draft and its files back), the stop, the queue (its drain when the run
//! ends, 바로 실행), attachments (picked, dropped, pasted), the slash palette, the pills' menus, the run settings, the status-line toggle,
//! a new conversation and the "send to" switch.
//!
//! What a later stage owns answers as C# does without it: a typed built-in, a guided question and a style's prompt are W9's (the text
//! goes to the agent as typed); the worktree send and held items are W7's; plugins, rename and the custom model dialog are W6's.

use std::collections::HashSet;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use base64::Engine as _;
use mighty_composer::ask_targets;
use mighty_composer::attachments;
use mighty_composer::menus::{self, MenuAction, MenuInputs, RunSettingsForm};
use mighty_composer::pending::SubmitReceipt;
use mighty_composer::queue::{self, QueueRunNowPath};
use mighty_composer::slash::{self, SlashCommandAction};
use mighty_core::attachment::RunAttachment;
use mighty_core::model::{LogEntry, RunSession, RunSettings};
use mighty_core::{time, wire};
use mighty_run::model::{RunEvent, RunStartReceipt, StartRunRequest};
use serde_json::{Value, json};

use crate::host::{Host, Registry, View, lock};
use crate::methods::{flag, text};
use crate::panes::{self, DRAFT_LIMIT, ensure};
use crate::runs;

/// `text[..input.MaxLength]`: at most 100,000 UTF-16 units, never half a pair.
pub fn clamp(text: &str) -> String {
    let mut units = 0;
    text.chars()
        .take_while(|c| {
            units += c.len_utf16();
            units <= DRAFT_LIMIT
        })
        .collect()
}

/// The pane's record, cloned.
pub fn session(view: &View, id: &str) -> Result<Arc<RunSession>, String> {
    view.snapshot.session(id).cloned().ok_or_else(|| "Session not found.".to_string())
}

/// C# `Change(update)`: the pane's record changed through the service (normalized, saved).
pub fn change(view: &mut View, id: &str, update: impl FnOnce(&RunSession) -> RunSession) -> Result<(), String> {
    view.update(|state| {
        if let Some(pane) = state.sessions_mut().iter_mut().find(|pane| pane.id() == id) {
            *pane = Arc::new(update(pane));
        }
    })?;
    if let Some(ui) = view.panes.get_mut(id) {
        ui.dirty = true;
    }
    view.mighty.dirty(id);
    Ok(())
}

/// C# `SlashNote`: a line the app itself says in the pane's transcript.
pub fn slash_note(view: &mut View, id: &str, text: &str) -> Result<(), String> {
    change(view, id, |pane| {
        let mut next = pane.clone();
        let entry = LogEntry::new(wire::new_id(), "system", text.to_string(), time::now_o(), pane.provider.clone());
        next.logs.get_or_insert_with(Vec::new).push(entry);
        next
    })
}

/// The pane's run settings, or C#'s defaults.
fn settings(pane: &RunSession) -> RunSettings {
    pane.settings.clone().unwrap_or_default()
}

/// Whether the service runs the pane now (`IsSessionRunning`).
fn running(host: &Host, id: &str) -> bool {
    host.runs().is_some_and(|r| r.service.is_session_running(id))
}

/// The menus' inputs for a pane (`WebBuildMenu`).
fn with_menu_inputs<T>(host: &Host, view: &View, pane: &RunSession, read: impl FnOnce(&MenuInputs) -> T) -> T {
    let workspace = view.snapshot.workspace(pane.workspace_id()).cloned();
    let runs = host.runs();
    let runtime = runs.and_then(|r| r.composer_runtime(pane.provider()));
    let efforts = runs.map(|r| r.efforts(pane, workspace.as_ref(), &view.snapshot)).unwrap_or_default();
    let inputs = MenuInputs {
        locale: host.locale,
        pane,
        runtime: runtime.as_ref(),
        efforts: &efforts,
        remote_session: workspace.as_ref().is_some_and(|w| w.remote.is_some()),
        refreshing_models: false,
        style_rows: None,
    };
    read(&inputs)
}

// ── starting a request ──────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `StartFromComposer`: the pane's request with `text` and `files` through the run service. Ok(Some(entry id, graph run id)) when
/// it started, Ok(None) when it was stopped before it started. `continuation`: a queued Antigravity card's rules and card.
async fn start_run(
    host: &Arc<Host>,
    id: &str,
    text: &str,
    files: Vec<RunAttachment>,
    permission_override: Option<String>,
    continuation: Option<(mighty_run::antigravity::AllowGrant, mighty_run::antigravity::PermissionRequest)>,
) -> Result<Option<(Option<String>, Option<String>)>, String> {
    let engine = host.engine()?;
    let receipt = Arc::new(Mutex::new(RunStartReceipt::default()));
    reset_unavailable_agy_model(host, id)?;
    let request = {
        let view = host.view();
        let pane = session(&view, id)?;
        let workspace = view.snapshot.workspace(pane.workspace_id()).cloned();
        let mut request = StartRunRequest::new(pane.id(), pane.workspace_id(), pane.kind(), text);
        request.registered_models = runs::registered_models_for(pane.provider(), workspace.as_ref(), &view.snapshot);
        request.model = pane.model().to_string();
        request.provider = pane.provider().to_string();
        request.settings = pane.settings.clone();
        request.resume_id = pane.resume_id.clone();
        request.attachments = (!files.is_empty()).then_some(files);
        request.permission_mode_override = permission_override;
        request.receipt = Some(receipt.clone());
        if let Some((grant, card)) = continuation {
            request.antigravity_grant = Some(grant);
            request.antigravity_card = Some(card);
        }
        request
    };
    match engine.service.start(request).await {
        Ok(Some(())) => {
            let made = lock(&receipt);
            Ok(Some((made.entry_id.clone(), made.graph_run_id.clone())))
        }
        Ok(None) => Ok(None),
        Err(error) => Err(error.message().to_string()),
    }
}

/// C# `PrepareStyleRunRequest`'s Antigravity part (MainWindow.Styles.cs:571, M/AppStore.swift:981-988): agy refuses a model its live
/// list lacks, so the run starts on its default instead, the pane's model set back with a note.
fn reset_unavailable_agy_model(host: &Host, id: &str) -> Result<(), String> {
    let mut view = host.view();
    let pane = session(&view, id)?;
    if pane.kind() != "claude" || pane.provider() != "gemini" {
        return Ok(());
    }
    let Some(catalog) = host.runs().and_then(|r| r.runtime("gemini")).map(|r| r.model_catalog) else { return Ok(()) };
    if !mighty_run::antigravity::model_unavailable(pane.model(), &catalog) {
        return Ok(());
    }
    let missing = pane.model().to_string();
    let note = host.locale.get_with("run.notice.antigravityModelReset", &[("model", &missing)]);
    change(&mut view, id, |p| {
        let mut next = p.clone();
        next.model = Some("default".into());
        next.logs.get_or_insert_with(Vec::new).push(LogEntry::new(wire::new_id(), "system", note.clone(), time::now_o(), None));
        next
    })
}

/// C# `PaneView.Submit` (MainWindow.cs:533): the editor's text and attachments go the way the spec orders it — a busy pane's queue,
/// then a start. What happened: started, notStarted, queued, blocked or none, with the request's user entry and graph run when it
/// started at once.
async fn submit(host: &Arc<Host>, id: &str) -> Result<SubmitReceipt, String> {
    // A saved "send to" choice not yet known to stand waits briefly for the pane's catalogue and the PATH read (ask.rs).
    crate::ask::await_targets(host, id).await;
    // Asking another CLI carries the words alone, wrapped once here: staged files stay in the composer.
    let asked = {
        let view = host.view();
        if !panes::alive(&view, id) {
            return Ok(SubmitReceipt::new("none"));
        }
        let pane = session(&view, id)?;
        crate::ask::send_target(host, &view.snapshot, &pane)
    };
    // DeferBusyComposer: a busy pane (running, starting) always queues the draft as its next request.
    let taken = {
        let mut view = host.view();
        let pane = session(&view, id)?;
        let busy_run = pane.status() == "running" || running(host, id);
        let ui = panes::ui(&mut view, id)?;
        if asked.is_some() && ask_targets::wrap(&ui.view.draft, asked).is_none() {
            return Ok(SubmitReceipt::new("none"));
        }
        if busy_run || ui.view.starting {
            if pane.kind() != "claude" || !ui.has_content() || ui.view.starting || ui.view.attachments_loading {
                return Ok(SubmitReceipt::new("none"));
            }
            let Some(text) = ask_targets::wrap(&ui.view.draft, asked) else { return Ok(SubmitReceipt::new("none")) };
            let files = if asked.is_none() { ui.view.attachments.clone() } else { vec![] };
            // The same request validation applies before consuming a draft even though it runs later.
            let mut probe = StartRunRequest::new(pane.id(), pane.workspace_id(), pane.kind(), &text);
            probe.provider = pane.provider().to_string();
            probe.model = pane.model().to_string();
            probe.settings = pane.settings.clone();
            probe.attachments = (!files.is_empty()).then(|| files.clone());
            probe.validate().map_err(|e| e.message().to_string())?;
            let override_mode = queue::background::queued_override(ui.view.launches_in_plan_mode);
            let consumed: HashSet<String> = files.iter().map(|f| f.id.clone()).collect();
            ui.queue.add(&text, files, override_mode, false, false, &host.locale).map_err(|e| e.to_string())?;
            ui.view.attachments.retain(|a| !consumed.contains(&a.id));
            ui.view.draft.clear();
            ui.dirty = true;
            let drained = matches!(pane.status(), "completed" | "idle");
            drop(view);
            if let Some(runs) = host.runs() {
                let _ = runs.service.update_draft(id, "");
            }
            host.view().sync();
            // The stream may finish while the draft is being taken.
            if drained {
                drain_queue(host.clone(), id.to_string());
            }
            return Ok(SubmitReceipt::new("queued"));
        }
        let Some((_, refreshed)) = panes::refreshed(host, &mut view, id) else { return Ok(SubmitReceipt::new("none")) };
        let ui = panes::ui(&mut view, id)?;
        if !refreshed.can_send || ui.view.starting {
            ui.dirty = true;
            return Ok(SubmitReceipt::new("blocked"));
        }
        // The draft and its attachments are taken and cleared at submit: what is typed meanwhile is the next draft.
        ui.view.starting = true;
        ui.submission += 1;
        let typed = std::mem::take(&mut ui.view.draft);
        let files = if asked.is_none() { std::mem::take(&mut ui.view.attachments) } else { vec![] };
        ui.dirty = true;
        (typed, files, ui.submission)
    };
    let (typed, files, submission) = taken;
    let submitted = ask_targets::wrap(&typed, asked).unwrap_or_else(|| typed.clone());
    // The model list as the CLI has it now comes first: the request takes the pane's model and effort after it (models.rs).
    crate::models::prepare(host, id).await;
    let moot = {
        let view = host.view();
        !panes::alive(&view, id) || view.panes.get(id).is_none_or(|u| u.submission != submission)
    };
    let outcome = if moot { None } else { Some(start_run(host, id, &submitted, files.clone(), None, None).await) };
    let mut view = host.view();
    let alive = panes::alive(&view, id);
    let started = matches!(outcome, Some(Ok(Some(_))));
    if let Some(ui) = view.panes.get_mut(id) {
        ui.view.starting = false;
        ui.dirty = true;
        // A start that did not happen puts the draft back where nothing new was typed, and its attachments beside what was added.
        if !started && alive {
            if ui.view.draft.is_empty() {
                ui.view.draft = typed.clone();
            }
            let kept: HashSet<String> = ui.view.attachments.iter().map(|a| a.id.clone()).collect();
            let back: Vec<RunAttachment> = files.into_iter().filter(|f| !kept.contains(&f.id)).collect();
            ui.view.attachments.splice(0..0, back);
        }
    }
    // A send made moot while the models were checked (a stop, another send, the pane gone) starts nothing.
    let Some(outcome) = outcome else { return Ok(SubmitReceipt::new("none")) };
    match outcome {
        Err(error) => Err(error),
        Ok(None) => {
            view.error = Some(host.locale.get("remote.error.startFailed"));
            Ok(SubmitReceipt::new("notStarted"))
        }
        Ok(Some((entry, run))) => {
            let moot = view.panes.get(id).is_none_or(|u| u.submission != submission);
            // Only the submitted draft is consumed: a newer draft typed while start was awaiting remains untouched.
            if !moot && view.panes.get(id).is_some_and(|u| u.view.draft.is_empty()) && view.snapshot.session(id).is_some() {
                change(&mut view, id, |p| RunSession { draft: Some(String::new()), ..p.clone() })?;
            }
            Ok(SubmitReceipt::with_ids("started", entry.as_deref(), run.as_deref()))
        }
    }
}

/// C# `ReceiveQueueRunEvent` (MainWindow.QueuedComposer.cs:156): a run's status settles what waited behind it — a stop cancels it
/// (the transcript says how many), an error holds it, an end drains it. True when the queue should drain now.
/// C# `PaneView.CustomModel` (MainWindow.cs:986-993, dialog D1): the model id typed in a styled sheet, refused with its note until it
/// is a model name (`custom_model::accept`, the composer's `ChangeModel` rule), then the pane's model; nothing while the pane runs.
pub fn open_custom_model(host: &Arc<Host>, id: String) {
    use mighty_native::surface::{DialogButton, DialogResult, DialogSpec};
    use mighty_native::ui::{Content, El, Local, Wrapping};
    let dialog = host.view().snapshot.session(&id).and_then(|pane| mighty_composer::custom_model::dialog(pane, &host.locale));
    let Some(dialog) = dialog else { return };
    let (typed, note) = (Local::new(dialog.value.clone()), Local::new(String::new()));
    let (field_text, note_text, header) = (typed.clone(), note.clone(), dialog.header.clone());
    let mut spec = DialogSpec::new(dialog.title.clone(), move |_| {
        let into = field_text.clone();
        let field = El::text_box(field_text.get()).header(header.clone()).on_text(move |words| into.set(words));
        let mut validation = El::text_block(note_text.get());
        if let Some(text) = validation.text_mut() {
            text.wrapping = Wrapping::Wrap;
        }
        Content::El(Box::new(El::stack(false, mighty_core::design_tokens::metrics::SPACING_SM, vec![field, validation])))
    })
    .styled(None, None);
    spec.primary = Some(DialogButton::new(dialog.select.clone()));
    spec.close = Some(dialog.cancel.clone());
    let (checked, locale) = (typed.clone(), host.locale);
    spec.accept = Some(Box::new(move |answer| {
        if answer != DialogResult::Primary {
            return true;
        }
        match mighty_composer::custom_model::accept(&checked.get(), &locale) {
            Ok(_) => true,
            Err(words) => {
                note.set(words);
                false
            }
        }
    }));
    let host = host.clone();
    tauri::async_runtime::spawn(async move {
        if crate::native::show_dialog(&host, spec).await != DialogResult::Primary {
            return;
        }
        let Ok(model) = mighty_composer::custom_model::accept(&typed.get(), &host.locale) else { return };
        let _ = crate::frame::act(&host, async {
            let mut view = host.view();
            change_model(&host, &mut view, &id, &model)
        })
        .await;
    });
}

/// `sessionInfo{paneId}` (C# `WebSessionInfo`, MainWindow.WebShell.Composer.cs:499-538): the session info popover's rows, made by
/// mighty-composer's `session_info` from the pane, its workspace and the model list as the CLI has it.
pub fn session_info(host: &Arc<Host>, pane: Option<&str>) -> crate::host::Reply {
    let mut view = host.view();
    let id = ensure(&mut view, pane)?;
    let pane = session(&view, &id)?;
    let workspace = view.snapshot.workspace(pane.workspace_id()).cloned();
    let catalog = host.runs().map(|r| r.catalog_of(pane.provider())).unwrap_or_else(|| mighty_run::providers::fallback(pane.provider()));
    let models: Vec<_> = catalog.models.iter().map(|m| m.label_option()).collect();
    let elapsed = pane.run_timing.as_ref().filter(|t| t.is_valid()).map(|t| mighty_run::timing::label(t, None));
    let grouping = crate::platform::number_grouping();
    Ok(mighty_composer::session_info::session_info(&mighty_composer::session_info::Inputs {
        locale: host.locale,
        pane: &pane,
        workspace: workspace.as_ref(),
        models: &models,
        elapsed,
        grouping: grouping.as_ref(),
        long_time: &crate::platform::long_time,
    }))
}

pub fn run_event(view: &mut View, event: &RunEvent) -> bool {
    let id = event.session_id.as_str();
    view.mighty.dirty(id);
    let Some(ui) = view.panes.get_mut(id) else { return false };
    ui.dirty = true;
    if event.kind != "status" {
        return false;
    }
    let status = event.status.as_deref().unwrap_or("");
    let kept = status == "stopped" && ui.queue_kept_on_stop;
    if matches!(status, "stopped" | "completed" | "idle" | "error") {
        ui.queue_kept_on_stop = false;
    }
    let locale = mighty_run::strings::locale();
    let notice = queue::settle_notice(status, ui.queue.items().len(), kept, &locale);
    let drain = ui.queue.settle(status, kept);
    ui.forget_unqueued();
    if let Some(notice) = notice {
        let _ = slash_note(view, id, &notice);
    }
    drain
}

/// C# `queueDrainTimer` + `StartNextQueuedInput`: once the pane is no longer running, its first queued request starts (the item a
/// start did not take stays at the head).
pub fn drain_queue(host: Arc<Host>, id: String) {
    tauri::async_runtime::spawn(async move {
        for _ in 0..600 {
            tokio::time::sleep(Duration::from_millis(100)).await;
            let next = {
                let mut view = host.view();
                if !panes::alive(&view, &id) {
                    return;
                }
                let status = view.snapshot.session(&id).map(|s| s.status().to_string()).unwrap_or_default();
                // C# `StartNextQueuedInput`: an update of the pane's CLI holds a queue already draining (updates.rs, W6-D).
                let held = view.snapshot.session(&id).is_some_and(|pane| {
                    crate::updates::holds(&host, &view.snapshot, pane)
                        || crate::updates::manual_block(&host, &view.snapshot, pane).is_some()
                });
                let Ok(ui) = panes::ui(&mut view, &id) else { return };
                if ui.queue.items().is_empty() {
                    return;
                }
                if status == "running" || status == "error" || ui.view.starting || ui.view.queue_starting || running(&host, &id) || held {
                    continue;
                }
                ui.view.queue_starting = true;
                ui.dirty = true;
                ui.queue.items()[0].id.clone()
            };
            crate::models::prepare(&host, &id).await;
            // A stop, a removal or the pane's close while the models were checked starts nothing.
            let taken = {
                let mut view = host.view();
                let alive = panes::alive(&view, &id);
                still_queued(view.panes.get_mut(&id), alive, &next)
            };
            let Some((next, continuation)) = taken else { return };
            let outcome =
                start_run(&host, &id, &next.text, next.attachments.clone(), next.permission_mode_override.clone(), continuation).await;
            let failed_card = {
                let mut view = host.view();
                let mut failed_card = None;
                if let Some(ui) = view.panes.get_mut(&id) {
                    ui.view.queue_starting = false;
                    ui.dirty = true;
                    if matches!(outcome, Ok(Some(_))) {
                        ui.queue.remove(&next.id);
                        ui.continuations.remove(&next.id);
                    } else if outcome.is_err()
                        && let Some((_, card)) = ui.continuations.remove(&next.id)
                    {
                        // S-L3: a continuation that could not start loses its grant with its place in the queue; its card decides again.
                        ui.queue.remove(&next.id);
                        failed_card = Some(card);
                    }
                }
                if let Err(error) = &outcome {
                    view.error = Some(error.clone());
                }
                failed_card
            };
            if let (Some(card), Err(error)) = (failed_card, &outcome) {
                crate::antigravity::continuation_failed(&host, &id, card, error);
            }
            return;
        }
    });
}

/// A queued Antigravity card's rules and card (`QueuedComposerInput.AntigravityGrant`/`AntigravityCard`).
type Continuation = (mighty_run::antigravity::AllowGrant, mighty_run::antigravity::PermissionRequest);

/// C# `TakeNextQueuedInput` once `PrepareModels` is over (MainWindow.QueuedComposer.cs:456-457): the item the queue's start took, read
/// again from the queue with its continuation (the decision made when it was queued wins; an Antigravity card's continuation carries
/// its rules). None when the pane went (`QueuePaneAlive`) or the item left the queue meanwhile (removed, or cancelled by a stop): the
/// start is given up, `queue_starting` cleared and the composer drawn again.
fn still_queued(ui: Option<&mut panes::PaneUi>, alive: bool, item: &str) -> Option<(queue::QueuedComposerInput, Option<Continuation>)> {
    let ui = ui?;
    let queued = ui.queue.items().iter().find(|q| q.id == item).filter(|_| alive).cloned();
    let Some(queued) = queued else {
        ui.view.queue_starting = false;
        ui.dirty = true;
        return None;
    };
    let continuation = ui.continuations.get(&queued.id).cloned();
    Some((queued, continuation))
}

// ── attachments ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `LoadAttachments(read)`: one read at a time, a shell pane none; the combined count is the composer's own limit; what goes
/// wrong says so in the composer.
async fn load_attachments(
    host: &Arc<Host>,
    id: &str,
    read: impl std::future::Future<Output = Result<Vec<RunAttachment>, String>>,
) -> Result<(), String> {
    {
        let mut view = host.view();
        let kind = session(&view, id)?.kind().to_string();
        let ui = panes::ui(&mut view, id)?;
        if ui.view.attachments_loading || kind == "shell" {
            return Ok(());
        }
        ui.view.attachments_loading = true;
        ui.view.attachment_error = None;
        ui.dirty = true;
    }
    let files = read.await;
    let mut view = host.view();
    let alive = panes::alive(&view, id);
    let Some(ui) = view.panes.get_mut(id) else { return Ok(()) };
    ui.view.attachments_loading = false;
    ui.dirty = true;
    if !alive {
        return Ok(());
    }
    let added = files.and_then(|files| {
        if ui.view.attachments.len() + files.len() > attachments::MAXIMUM_COUNT {
            return Err(host.locale.get("composer.attachment.tooMany"));
        }
        let combined: Vec<RunAttachment> = ui.view.attachments.iter().cloned().chain(files.iter().cloned()).collect();
        attachments::validate(&combined, &host.locale)?;
        Ok(files)
    });
    match added {
        Ok(files) => ui.view.attachments.extend(files),
        Err(error) => ui.view.attachment_error = Some(error),
    }
    Ok(())
}

/// `WebAddAttachments`: files dropped or pasted in the page, as base64 (5 MB each, 8 MB in all, 8 files).
fn read_dropped(host: &Host, kind: &str, files: &[(String, String)]) -> Result<Vec<RunAttachment>, String> {
    let l = &host.locale;
    if kind == "shell" {
        return Err(l.get("wire.startRun.attachmentAiOnly"));
    }
    if files.len() > attachments::MAXIMUM_COUNT {
        return Err(l.get("composer.attachment.tooMany"));
    }
    let mut read = Vec::new();
    let mut total = 0usize;
    for (name, data) in files {
        if data.len() > attachments::MAXIMUM_FILE_BYTES.div_ceil(3) * 4 + 4 {
            return Err(l.get("composer.attachment.fileTooLarge"));
        }
        let bytes = base64::engine::general_purpose::STANDARD.decode(data).map_err(|_| l.get("composer.attachment.readIncomplete"))?;
        if bytes.len() > attachments::MAXIMUM_FILE_BYTES {
            return Err(l.get("composer.attachment.fileTooLarge"));
        }
        total += bytes.len();
        if total > attachments::MAXIMUM_TOTAL_BYTES {
            return Err(l.get("attachments.error.totalTooLarge"));
        }
        let file = std::path::Path::new(name).file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| name.clone());
        read.push(attachments::make(&file, &bytes, l)?);
    }
    Ok(read)
}

/// `AttachmentInput.ReadFilesAsync` over the files the OS's picker gave.
fn read_picked(host: &Host, paths: &[std::path::PathBuf]) -> Result<Vec<RunAttachment>, String> {
    let l = &host.locale;
    if paths.len() > attachments::MAXIMUM_COUNT {
        return Err(l.get("composer.attachment.tooMany"));
    }
    let mut read = Vec::new();
    let mut total = 0u64;
    for path in paths {
        let size = std::fs::metadata(path).map_err(|e| e.to_string())?.len();
        if size > attachments::MAXIMUM_FILE_BYTES as u64 {
            return Err(l.get("composer.attachment.fileTooLarge"));
        }
        total += size;
        if total > attachments::MAXIMUM_TOTAL_BYTES as u64 {
            return Err(l.get("attachments.error.totalTooLarge"));
        }
        let bytes = std::fs::read(path).map_err(|_| l.get("composer.attachment.readIncomplete"))?;
        let name = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| "attachment".into());
        read.push(attachments::make(&name, &bytes, l)?);
    }
    Ok(read)
}

// ── settings ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `ChangeSettings`: not while the pane runs.
fn change_settings(view: &mut View, id: &str, update: impl FnOnce(&RunSettings) -> RunSettings) -> Result<(), String> {
    let pane = session(view, id)?;
    if pane.status() == "running" {
        return Ok(());
    }
    change(view, id, |p| RunSession { settings: Some(update(&settings(p))), ..p.clone() })
}

/// C# `ChangeModel`: a valid name; the effort kept only where the new model takes it.
fn change_model(host: &Host, view: &mut View, id: &str, value: &str) -> Result<(), String> {
    let pane = session(view, id)?;
    if pane.status() == "running" {
        return Ok(());
    }
    if !wire::model(value) {
        return Err(host.locale.get("composer.model.invalidName"));
    }
    let candidate = RunSession { model: Some(value.to_string()), ..(*pane).clone() };
    let workspace = view.snapshot.workspace(pane.workspace_id()).cloned();
    let efforts = host.runs().map(|r| r.efforts(&candidate, workspace.as_ref(), &view.snapshot)).unwrap_or_default();
    change(view, id, |p| {
        let mut set = settings(p);
        if !efforts.contains(&set.effort) {
            set.effort = "default".into();
        }
        RunSession { model: Some(value.to_string()), settings: Some(set), ..p.clone() }
    })
}

/// C# `SessionTemplate.SwitchProvider`: the new agent with its own defaults, the switch said in the transcript.
fn switch_provider(host: &Host, pane: &RunSession, provider: &str) -> RunSession {
    if !wire::PROVIDERS.contains(&provider) || pane.provider() == provider {
        return pane.clone();
    }
    let label = mighty_core::model_label::provider_label(provider);
    let line = host.locale.get_with("pane.log.switchedProvider", &[("provider", label)]);
    let mut next = pane.clone();
    if pane.title.as_deref() == Some(mighty_run::providers::name(pane.provider())) {
        next.title = Some(mighty_run::providers::name(provider).to_string());
    }
    next.provider = Some(provider.to_string());
    next.model = Some("default".into());
    next.settings = Some(RunSettings::default());
    next.resume_id = None;
    next.session_usage = None;
    next.ask_target = None;
    next.logs.get_or_insert_with(Vec::new).push(LogEntry::new(wire::new_id(), "system", line, time::now_o(), Some(provider.to_string())));
    next
}

/// C# `ResetConversationAsync` (MainWindow.WorkflowParity.cs:35): a pane at rest forgets its CLI conversation; false otherwise.
fn reset_conversation(host: &Host, view: &mut View, id: &str) -> Result<bool, String> {
    if !panes::alive(view, id) {
        return Ok(false);
    }
    let pane = session(view, id)?;
    let busy = view.panes.get(id).is_some_and(|u| u.view.starting || u.view.queue_starting);
    if busy || running(host, id) || pane.status() == "running" {
        return Ok(false);
    }
    let note = host.locale.get("composer.newConversationNote");
    change(view, id, |p| {
        let mut next = p.clone();
        next.resume_id = None;
        next.session_usage = None;
        next.logs.get_or_insert_with(Vec::new).push(LogEntry::new(wire::new_id(), "system", note, time::now_o(), p.provider.clone()));
        next
    })?;
    Ok(true)
}

/// The settings `StartRunRequest.Validate` accepts for the pane (the run settings popover's last check).
fn validate_settings(pane: &RunSession, setting: &RunSettings) -> Result<(), String> {
    let mut probe = StartRunRequest::new(pane.id(), pane.workspace_id(), pane.kind(), "x");
    probe.provider = pane.provider().to_string();
    probe.model = pane.model().to_string();
    probe.settings = Some(setting.clone());
    probe.validate().map(|_| ()).map_err(|e| e.message().to_string())
}

/// A pane's models read again in the background (`RefreshPaneModels`) through the model lists' own refresh, which reconciles the panes
/// with the list (models.rs); the composer told when they are in.
fn refresh_models(host: &Arc<Host>, view: &View, id: &str) {
    if view.snapshot.session(id).is_none() {
        return;
    }
    let (host, id) = (host.clone(), id.to_string());
    tauri::async_runtime::spawn(async move {
        crate::models::reload(&host, &id).await;
        host.panes_changed();
    });
}

/// Runs a pill menu's row (`WebMenuChoose`'s `Func<Task>`).
fn run_menu_action(host: &Arc<Host>, view: &mut View, id: &str, action: MenuAction) -> Result<(), String> {
    let pane = session(view, id)?;
    let starting = view.panes.get(id).is_some_and(|u| u.view.starting);
    match action {
        MenuAction::ChangeProvider(value) => {
            if pane.status() == "running" || starting || pane.provider() == value {
                return Ok(());
            }
            let next = switch_provider(host, &pane, &value);
            change(view, id, |_| next)?;
            // The switch reads the new agent's models; the list in hand is another agent's (models.rs, C# RefreshModels).
            crate::models::refresh(host, &view.snapshot, id, true);
        }
        MenuAction::RefreshModels => refresh_models(host, view, id),
        MenuAction::ChangeModel(value) => change_model(host, view, id, &value)?,
        MenuAction::SetEffort(effort) => change_settings(view, id, |s| RunSettings { effort, ..s.clone() })?,
        MenuAction::SetPermission(mode) => {
            let codex = pane.provider() == "codex";
            change_settings(view, id, |s| RunSettings {
                network_access: codex && matches!(mode.as_str(), "acceptEdits" | "onRequest") && s.network_access,
                permission_mode: mode,
                ..s.clone()
            })?
        }
        MenuAction::ToggleFast => {
            let fast =
                host.runs().and_then(|r| r.runtime(pane.provider())).map_or(pane.provider() == "codex", |r| r.capabilities.fast_mode);
            change_settings(view, id, |s| RunSettings { fast_mode: !s.fast_mode && fast, ..s.clone() })?
        }
        MenuAction::NewConversation => {
            reset_conversation(host, view, id)?;
        }
        // The model menu's "enter id" row (C# `CustomModel`, dialog D1), on its own task: the dialog waits for its answer.
        MenuAction::CustomModel => open_custom_model(host, id.to_string()),
        // The plugin browser (plugins.rs, W6-D).
        MenuAction::OpenPlugins(provider) => {
            let (host, provider) = (host.clone(), provider.clone());
            tauri::async_runtime::spawn(async move { crate::plugins::open(&host, &provider).await });
        }
        // The page draws the run settings itself; rename and styles are later stages' (README).
        MenuAction::RunSettings | MenuAction::Rename | MenuAction::Style(_) => {}
    }
    Ok(())
}

/// C# `PerformSlashAction` (MainWindow.SlashPalette.cs:243): true when the action ran (the draft is then cleared).
fn slash_action(host: &Arc<Host>, view: &mut View, id: &str, action: SlashCommandAction, argument: Option<&str>) -> Result<bool, String> {
    let pane = session(view, id)?;
    if pane.kind() == "shell" {
        return Ok(false);
    }
    let l = host.locale;
    let busy = pane.status() == "running" || view.panes.get(id).is_some_and(|u| u.view.starting);
    match action {
        SlashCommandAction::NewConversation => {
            if busy {
                slash_note(view, id, &l.get("slash.note.newConversationRunning"))?;
            } else if pane.resume_id.is_none() {
                slash_note(view, id, &l.get("remote.error.nothingToResume"))?;
            } else {
                reset_conversation(host, view, id)?;
            }
        }
        SlashCommandAction::Help => slash_note(view, id, &slash::help_text(pane.provider(), &l))?,
        SlashCommandAction::SetModel => {
            let Some(model) = argument else { return Ok(false) };
            if busy {
                slash_note(view, id, &l.get("slash.note.modelRunning"))?;
                return Ok(true);
            }
            let catalog =
                host.runs().map(|r| r.catalog_of(pane.provider())).unwrap_or_else(|| mighty_run::providers::fallback(pane.provider()));
            let options: Vec<_> = catalog.models.iter().map(|m| m.label_option()).collect();
            let label =
                mighty_core::model_label::selection(&RunSession { model: Some(model.to_string()), ..(*pane).clone() }, &options, &l);
            if pane.model() == model {
                slash_note(view, id, &l.get("slash.note.modelAlready").replace("{name}", &label))?;
                return Ok(true);
            }
            change_model(host, view, id, model)?;
            slash_note(view, id, &l.get_with("slash.note.modelChanged", &[("name", &label)]))?;
        }
        SlashCommandAction::SetPermission => {
            let Some(mode) = argument else { return Ok(false) };
            let label = mighty_composer::runtime::permission_label(pane.provider(), mode, &l);
            if busy {
                slash_note(view, id, &l.get("slash.note.permissionRunning"))?;
                return Ok(true);
            }
            if settings(&pane).permission_mode == mode {
                slash_note(view, id, &l.get("slash.note.permissionAlready").replace("{label}", &label))?;
                return Ok(true);
            }
            let codex = pane.provider() == "codex";
            change_settings(view, id, |s| RunSettings {
                network_access: codex && matches!(mode, "acceptEdits" | "onRequest") && s.network_access,
                permission_mode: mode.to_string(),
                ..s.clone()
            })?;
            if view.snapshot.session(id).is_some_and(|p| settings(p).permission_mode == mode) {
                slash_note(view, id, &l.get_with("slash.note.permissionChanged", &[("label", &label)]))?;
            }
        }
        // The pane's rename sheet (frame.rs); it answers on its own while the typed command clears.
        SlashCommandAction::Rename => crate::frame::start_rename_session(host, id.to_string()),
        // C# `PerformSlashAction`: Settings (settings/mod.rs) and the pane's plugin browser (plugins.rs, W6-D), each on its own task.
        SlashCommandAction::OpenSettings => {
            let host = host.clone();
            tauri::async_runtime::spawn(async move { crate::settings::open(&host, None) });
        }
        SlashCommandAction::OpenPlugins => {
            let (host, provider) = (host.clone(), session(view, id)?.provider().to_string());
            tauri::async_runtime::spawn(async move { crate::plugins::open(&host, &provider).await });
        }
        // `ShowContext`: the session info popover, which the page opens itself (`sessionInfo`).
        SlashCommandAction::ShowUsage => return Ok(false),
    }
    Ok(true)
}

// ── the methods ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

fn pane_id(args: &Value) -> Option<&str> {
    text(args, "paneId")
}

/// A pane's stop under way: its composer's `stopping` is cleared when this goes, whichever way the stop ended.
struct Stopping {
    host: Arc<Host>,
    id: String,
}

impl Drop for Stopping {
    fn drop(&mut self) {
        if let Some(ui) = self.host.view().panes.get_mut(&self.id) {
            ui.view.stopping = false;
            ui.dirty = true;
        }
    }
}

pub fn register(methods: &mut Registry) {
    methods.add("composerState", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, pane_id(&args))?;
        Ok(panes::composer_state(&host, &mut view, &id).map_or(Value::Null, |s| serde_json::to_value(s).unwrap_or(Value::Null)))
    });
    methods.add("updateDraft", |host: Arc<Host>, args| async move {
        let id = {
            let mut view = host.view();
            let id = ensure(&mut view, pane_id(&args))?;
            let ui = panes::ui(&mut view, &id)?;
            ui.view.draft = clamp(text(&args, "text").unwrap_or(""));
            ui.dirty = true;
            (id, ui.view.draft.clone())
        };
        // DesktopService.UpdateDraft: the pane alone, saved on the schedule.
        if let Some(runs) = host.runs()
            && runs.service.update_draft(&id.0, &id.1).map_err(|e| e.to_string())?
        {
            host.view().sync();
        }
        Ok(Value::Null)
    });
    methods.add("send", |host: Arc<Host>, args| async move {
        let id = {
            let mut view = host.view();
            let id = ensure(&mut view, pane_id(&args))?;
            let ui = panes::ui(&mut view, &id)?;
            ui.view.draft = clamp(text(&args, "text").unwrap_or(""));
            // WebKeepAttachments: only the attachments the page still shows go with a send.
            if let Some(ids) = args.get("attachments").and_then(Value::as_array) {
                let keep: HashSet<&str> = ids.iter().filter_map(Value::as_str).collect();
                ui.view.attachments.retain(|a| keep.contains(a.id.as_str()));
            }
            ui.dirty = true;
            id
        };
        let receipt = submit(&host, &id).await?;
        let mut view = host.view();
        let draft = view.panes.get(&id).map(|u| u.view.draft.clone()).unwrap_or_default();
        // C# `view.WebComposerState()`: the answer always carries the composer, else the pane is gone.
        let state = panes::composer_state(&host, &mut view, &id).ok_or_else(|| "Session not found.".to_string())?;
        Ok(serde_json::to_value(receipt.send_answer(text(&args, "clientId"), &draft, Some(state))).unwrap_or(Value::Null))
    });
    methods.add("stop", |host: Arc<Host>, args| async move {
        let (id, noted) = {
            let mut view = host.view();
            let id = ensure(&mut view, pane_id(&args))?;
            let work = session(&view, &id)?.background_work.clone();
            let ui = panes::ui(&mut view, &id)?;
            if ui.view.stopping {
                return Ok(Value::Null);
            }
            ui.submission += 1;
            // StopActiveRun: a stop cancels what waited unless the turn only waits on background work (queueKeptOnStop).
            ui.queue_kept_on_stop = queue::background::stop_keeps_queue(work.as_ref());
            let cancelled = (!ui.queue_kept_on_stop).then(|| ui.queue.items().len()).filter(|n| *n > 0);
            if !ui.queue_kept_on_stop {
                ui.queue.clear();
                ui.forget_unqueued();
            }
            ui.view.stopping = true;
            ui.dirty = true;
            let noted = match cancelled.and_then(|count| queue::settle_notice("stopped", count, false, &host.locale)) {
                Some(notice) => slash_note(&mut view, &id, &notice),
                None => Ok(()),
            };
            (id, noted)
        };
        // From here `stopping` is cleared however the stop ends: done, refused, or a panic on the way.
        let _stopping = Stopping { host: host.clone(), id: id.clone() };
        noted?;
        host.engine()?.service.stop(&id).await.map(|_| Value::Null).map_err(|e| e.message().to_string())
    });
    methods.add("queueRemove", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, pane_id(&args))?;
        let ui = panes::ui(&mut view, &id)?;
        if ui.queue.remove(text(&args, "itemId").unwrap_or("")) {
            ui.forget_unqueued();
            ui.dirty = true;
        }
        Ok(Value::Null)
    });
    methods.add("queueRunNow", |host: Arc<Host>, args| async move {
        let item = text(&args, "itemId").unwrap_or("").to_string();
        let (id, path) = {
            let mut view = host.view();
            let id = ensure(&mut view, pane_id(&args))?;
            let rows = run_now_rows(&host, &mut view, &id);
            (id, rows.iter().find(|(row, _)| *row == item).map_or(QueueRunNowPath::Hidden, |(_, p)| *p))
        };
        match path {
            // At the head, the queue's own start takes it.
            QueueRunNowPath::Here => {
                if let Some(ui) = host.view().panes.get_mut(&id) {
                    ui.queue.move_to_front(&item);
                }
                drain_queue(host.clone(), id);
            }
            // SteerQueuedInput: into the run that only waits on background work, as a new turn.
            QueueRunNowPath::Steer => {
                let row = host.view().panes.get(&id).and_then(|u| u.queue.items().iter().find(|q| q.id == item).cloned());
                if let Some(row) = row
                    && row.attachments.is_empty()
                    && host.engine()?.service.try_steer(&id, &row.text)
                    && let Some(ui) = host.view().panes.get_mut(&id)
                {
                    ui.queue.remove(&row.id);
                    ui.dirty = true;
                }
            }
            // Worktrees and held items are delegation's (W7); the waits do nothing.
            _ => {}
        }
        Ok(Value::Null)
    });
    // The worktree menu's agents are delegation's (W7): no pane offers it yet (`OffersWorktreeMenu`).
    methods.add("queueRunNowMenu", |host: Arc<Host>, args| async move {
        ensure(&mut host.view(), pane_id(&args))?;
        Ok(json!([]))
    });
    // C# `installCli`: the setup screen on the item that installs the pane's missing CLI (setup.rs, W6-B).
    methods.add("installCli", |host: Arc<Host>, args| async move {
        let item = {
            let view = host.view();
            let pane = pane_id(&args).and_then(|id| view.snapshot.session(id)).cloned();
            pane.and_then(|pane| crate::setup::item_for_missing_cli(&host, &view.snapshot, &pane))
        };
        if let Some(item) = item {
            crate::setup::open(&host, Some(&item));
        }
        Ok(Value::Null)
    });
    methods.add("pickAttachments", |host: Arc<Host>, args| async move {
        let id = ensure(&mut host.view(), pane_id(&args))?;
        let picker = host.clone();
        let read = async move {
            use tauri_plugin_dialog::DialogExt;
            let (sender, receiver) = tokio::sync::oneshot::channel();
            let mut dialog = picker.app.dialog().file();
            // Owned by the app's window (C#'s FileOpenPicker is initialized with it): modal to it, in front of it.
            if let Some(window) = tauri::Manager::get_window(&picker.app, crate::host::WINDOW) {
                dialog = dialog.set_parent(&window);
            }
            dialog.pick_files(move |files| {
                let _ = sender.send(files);
            });
            let picked = receiver.await.ok().flatten().unwrap_or_default();
            let paths: Vec<std::path::PathBuf> = picked.into_iter().filter_map(|f| f.into_path().ok()).collect();
            read_picked(&picker, &paths)
        };
        load_attachments(&host, &id, read).await?;
        Ok(Value::Null)
    });
    methods.add("addAttachments", |host: Arc<Host>, args| async move {
        let (id, kind) = {
            let mut view = host.view();
            let id = ensure(&mut view, pane_id(&args))?;
            let kind = session(&view, &id)?.kind().to_string();
            (id, kind)
        };
        let files: Vec<(String, String)> = args
            .get("files")
            .and_then(Value::as_array)
            .map(|list| {
                list.iter()
                    .map(|f| (text(f, "name").unwrap_or("attachment").to_string(), text(f, "data").unwrap_or("").to_string()))
                    .collect()
            })
            .unwrap_or_default();
        let read = read_dropped(&host, &kind, &files);
        load_attachments(&host, &id, async move { read }).await?;
        Ok(Value::Null)
    });
    methods.add("removeAttachment", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, pane_id(&args))?;
        let ui = panes::ui(&mut view, &id)?;
        let gone = text(&args, "id").unwrap_or("");
        ui.view.attachments.retain(|a| a.id != gone);
        ui.view.attachment_error = None;
        ui.dirty = true;
        Ok(Value::Null)
    });
    methods.add("dismissAttachmentError", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, pane_id(&args))?;
        let ui = panes::ui(&mut view, &id)?;
        ui.view.attachment_error = None;
        ui.dirty = true;
        Ok(Value::Null)
    });
    methods.add("attachmentPreview", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, pane_id(&args))?;
        let ui = panes::ui(&mut view, &id)?;
        let wanted = text(&args, "id").unwrap_or("");
        let Some(file) = ui.view.attachments.iter().find(|a| a.id == wanted) else { return Ok(Value::Null) };
        let size = attachments::decoded_length(file);
        Ok(match file.media_type.as_str() {
            "image/png" | "image/jpeg" | "image/gif" | "image/webp" => json!({
                "name": file.name, "mediaType": file.media_type, "size": size,
                "image": format!("data:{};base64,{}", file.media_type, file.data_base64),
            }),
            "text/plain" => {
                let bytes = attachments::decode(file, &host.locale).unwrap_or_default();
                let units: Vec<u16> = String::from_utf8_lossy(&bytes).encode_utf16().take(20_000).collect();
                let text = String::from_utf16_lossy(&units);
                json!({"name": file.name, "mediaType": file.media_type, "size": size, "text": text})
            }
            _ => json!({"name": file.name, "mediaType": file.media_type, "size": size}),
        })
    });
    methods.add("slashCommands", |host: Arc<Host>, args| async move {
        let (pane, root) = {
            let mut view = host.view();
            let id = ensure(&mut view, pane_id(&args))?;
            let pane = session(&view, &id)?;
            let root = view.snapshot.workspace(pane.workspace_id()).filter(|w| w.remote.is_none()).map(|w| w.path().to_string());
            (pane, root)
        };
        // The catalogue scanned off the page's thread: the user's and the workspace's commands and skills.
        let locale = host.locale;
        let provider = pane.provider().to_string();
        let path = root.clone();
        let scanned = tauri::async_runtime::spawn_blocking(move || {
            let home = dirs_home();
            slash::commands(&provider, root.as_deref().map(std::path::Path::new), &home, &slash::DiskFiles, &locale)
        })
        .await;
        // The scan is the catalogue the "send to" switch reads too (ask.rs, C# `SetSlashCatalog`); a scan that panicked is no catalogue,
        // so the one in hand, and a saved "send to" choice it stands for, stay.
        if let Ok(scanned) = &scanned {
            crate::ask::set_catalog(&host, pane.provider(), path.as_deref(), scanned.clone());
        }
        let scanned = scanned.unwrap_or_default();
        let view = host.view();
        Ok(with_menu_inputs(&host, &view, &pane, |i| serde_json::to_value(menus::slash_catalog(i, &scanned)).unwrap_or(Value::Null)))
    });
    methods.add("slashAction", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, pane_id(&args))?;
        let Some(action) = text(&args, "action").and_then(SlashCommandAction::parse) else { return Err("Unknown action.".into()) };
        if slash_action(&host, &mut view, &id, action, text(&args, "arg"))? {
            let ui = panes::ui(&mut view, &id)?;
            ui.view.draft.clear();
            ui.dirty = true;
            if let Some(runs) = host.runs() {
                let _ = runs.service.update_draft(&id, "");
            }
            view.sync();
        }
        Ok(json!(view.panes.get(&id).map(|u| u.view.draft.clone()).unwrap_or_default()))
    });
    methods.add("composerMenu", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, pane_id(&args))?;
        let pane = session(&view, &id)?;
        let name = text(&args, "menu").unwrap_or("").to_string();
        let rows = with_menu_inputs(&host, &view, &pane, |i| menus::composer_menu(&name, i))?;
        let value = serde_json::to_value(&rows).unwrap_or(Value::Null);
        panes::ui(&mut view, &id)?.menu = Some((name, rows));
        Ok(value)
    });
    methods.add("composerMenuChoose", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, pane_id(&args))?;
        let pane = session(&view, &id)?;
        let name = text(&args, "menu").unwrap_or("").to_string();
        let path: Vec<i64> =
            args.get("path").and_then(Value::as_array).map(|l| l.iter().map(|i| i.as_i64().unwrap_or(-1)).collect()).unwrap_or_default();
        // The menu last listed, so a choice runs the row the page showed.
        let rows = match panes::ui(&mut view, &id)?.menu.take() {
            Some((kept, rows)) if kept == name => rows,
            _ => with_menu_inputs(&host, &view, &pane, |i| menus::composer_menu(&name, i))?,
        };
        if let Some(action) = menus::menu_action(&rows, &path)? {
            run_menu_action(&host, &mut view, &id, action)?;
        }
        if let Some(ui) = view.panes.get_mut(&id) {
            ui.dirty = true;
        }
        Ok(Value::Null)
    });
    methods.add("runSettings", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, pane_id(&args))?;
        let pane = session(&view, &id)?;
        Ok(with_menu_inputs(&host, &view, &pane, |i| serde_json::to_value(menus::run_settings_info(i)).unwrap_or(Value::Null)))
    });
    methods.add("applyRunSettings", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, pane_id(&args))?;
        let pane = session(&view, &id)?;
        let form = RunSettingsForm {
            fast_mode: flag(&args, "fastMode"),
            web_search: text(&args, "webSearch").unwrap_or("default").to_string(),
            network_access: flag(&args, "networkAccess"),
            max_turns: text(&args, "maxTurns").map(str::to_string),
            max_budget_usd: text(&args, "maxBudgetUsd").map(str::to_string),
        };
        let starting = view.panes.get(&id).is_some_and(|u| u.view.starting || u.view.queue_starting);
        let locked = starting || pane.status() == "running" || running(&host, &id);
        let alive = panes::alive(&view, &id);
        let validate = |setting: &RunSettings| validate_settings(&pane, setting);
        match menus::apply_run_settings(&pane, alive, locked, &form, &validate, &host.locale) {
            Err(refusal) => Ok(json!(refusal)),
            Ok(setting) => {
                change(&mut view, &id, |p| RunSession { settings: Some(setting), ..p.clone() })?;
                Ok(Value::Null)
            }
        }
    });
    methods.add("toggleFast", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, pane_id(&args))?;
        run_menu_action(&host, &mut view, &id, MenuAction::ToggleFast)?;
        Ok(Value::Null)
    });
    // The composer's status-line toggle of a Claude pane: an app-wide preference.
    methods.add("composerToggle", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        if text(&args, "which") == Some("statusLine") {
            let enabled = !view.snapshot.status_line_enabled;
            view.update(|state| state.status_line_enabled = enabled)?;
            for pane in view.panes.values_mut() {
                pane.dirty = true;
            }
        }
        Ok(Value::Null)
    });
    methods.add("newConversation", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, pane_id(&args))?;
        let reset = reset_conversation(&host, &mut view, &id)?;
        if reset {
            refresh_models(&host, &view, &id);
        }
        Ok(json!(reset))
    });
    // The 보낼 대상 switch (ask.rs, C# `SetAskTarget`): a target the pane offers, or null for Claude.
    methods.add("setAskTarget", |host: Arc<Host>, args| async move {
        let id = ensure(&mut host.view(), pane_id(&args))?;
        let target = match args.get("target") {
            Some(Value::String(target)) => Some(target.clone()),
            Some(Value::Null) => None,
            _ => return Err(host.locale.get("remote.error.badBody")),
        };
        crate::ask::set_target(&host, &id, target)?;
        Ok(Value::Null)
    });
    methods.add("dismissAskNotice", |host: Arc<Host>, args| async move {
        let id = ensure(&mut host.view(), pane_id(&args))?;
        crate::ask::dismiss_notice(&host, &id);
        Ok(Value::Null)
    });
}

/// C# `RunNowRows` for a pane with a view.
fn run_now_rows(host: &Host, view: &mut View, id: &str) -> Vec<(String, QueueRunNowPath)> {
    let Some(pane) = view.snapshot.session(id).cloned() else { return vec![] };
    let workspace = view.snapshot.workspace(pane.workspace_id()).cloned();
    let runs = host.runs();
    let runtime = runs.and_then(|r| r.composer_runtime(pane.provider()));
    let efforts = runs.map(|r| r.efforts(&pane, workspace.as_ref(), &view.snapshot)).unwrap_or_default();
    let window = mighty_composer::composer::WindowRules::default();
    let Some(ui) = view.panes.get_mut(id) else { return vec![] };
    ui.view.queue = ui.queue.items().to_vec();
    let inputs = mighty_composer::composer::ComposerInputs {
        locale: host.locale,
        now: time::DateTimeOffset::now(),
        pane: &pane,
        workspace: workspace.as_ref(),
        runtime: runtime.as_ref(),
        efforts: &efforts,
        view: &ui.view,
        window: &window,
    };
    mighty_composer::composer::run_now_rows(&inputs)
}

/// The user's home folder (the slash catalogue's `~/.claude`).
pub(crate) fn dirs_home() -> std::path::PathBuf {
    std::env::var_os("USERPROFILE").or_else(|| std::env::var_os("HOME")).map(std::path::PathBuf::from).unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use mighty_core::locale::Locale;
    use mighty_run::antigravity::{AllowGrant, PermissionRequest};

    use super::{panes, queue, still_queued};

    /// A pane view whose queue's start took its first item and waits for the models (`queue_starting`), a second item behind it; the
    /// first carries an Antigravity card's continuation.
    fn waiting() -> (panes::PaneUi, String, String) {
        let locale = Locale::new("en");
        let mut ui = panes::PaneUi::default();
        let first = ui.queue.add("first", vec![], None, false, true, &locale).unwrap().id;
        let second = ui.queue.add("second", vec![], Some("plan"), false, false, &locale).unwrap().id;
        let grant = AllowGrant { request_id: "agy-1".into(), rules: vec!["run_command(echo hi)".into()], temporary: true };
        let card = PermissionRequest { id: "agy-1".into(), run_id: "run-1".into(), actions: vec![], received_at: None };
        ui.continuations.insert(first.clone(), (grant, card));
        ui.view.queue_starting = true;
        ui.dirty = false;
        (ui, first, second)
    }

    /// Nothing happened while the models were checked: the item starts as it is queued now, with its Antigravity continuation.
    #[test]
    fn an_item_still_queued_starts_with_its_continuation() {
        let (mut ui, first, _) = waiting();
        let (item, continuation) = still_queued(Some(&mut ui), true, &first).expect("still queued");
        assert_eq!((item.id.as_str(), item.text.as_str()), (first.as_str(), "first"));
        let (grant, card) = continuation.expect("the card's continuation goes with it");
        assert_eq!((grant.request_id.as_str(), card.id.as_str()), ("agy-1", "agy-1"));
        assert!(ui.view.queue_starting, "the start goes on");
    }

    /// `queueRemove` while the models were checked (MainWindow.QueuedComposer.cs:457): the removed item never starts, the one behind it
    /// stays queued, and the composer is free again.
    #[test]
    fn an_item_removed_during_the_model_check_does_not_start() {
        let (mut ui, first, second) = waiting();
        assert!(ui.queue.remove(&first), "the page's queueRemove");
        assert!(still_queued(Some(&mut ui), true, &first).is_none(), "nothing starts");
        assert!(!ui.view.queue_starting && ui.dirty, "queue_starting cleared, the composer drawn again");
        assert_eq!(ui.queue.items().iter().map(|q| q.id.as_str()).collect::<Vec<_>>(), [second.as_str()], "the next one waits");
    }

    /// A stop while the models were checked cancels what waited (`StopActiveRun`): no cancelled request starts.
    #[test]
    fn a_stop_during_the_model_check_starts_no_cancelled_request() {
        let (mut ui, first, _) = waiting();
        // The page's stop, as the `stop` method does it for a turn that does not only wait on background work.
        ui.submission += 1;
        ui.queue_kept_on_stop = queue::background::stop_keeps_queue(None);
        if !ui.queue_kept_on_stop {
            ui.queue.clear();
        }
        assert!(still_queued(Some(&mut ui), true, &first).is_none(), "the cancelled request does not start");
        assert!(!ui.view.queue_starting && ui.dirty);
    }

    /// The pane closed while the models were checked (`QueuePaneAlive`): nothing starts, whether its view is gone too or not yet.
    #[test]
    fn a_pane_closed_during_the_model_check_starts_nothing() {
        let (mut ui, first, _) = waiting();
        assert!(still_queued(Some(&mut ui), false, &first).is_none(), "the pane left the state");
        assert!(!ui.view.queue_starting && ui.dirty);
        assert!(still_queued(None, false, &first).is_none(), "its view is gone");
    }
}
