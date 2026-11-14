//! The Mighty views' page methods and push (C# MainWindow.WebShell.Mighty.cs): `mightyVisible` starts and stops a pane's pushes,
//! [`flush`] projects each shown pane at most once per 120 ms tick (a pane whose record changed, one a page method marked, and every
//! one each eighth tick) with mighty-graph's `view::project` and sends only what changed (`MightyPushes`); the page's methods change
//! the pane's record or its view's state (`MightyPane`) and mark it. The result files a projection names are looked through off the
//! page's thread and handed back (`LookThroughResultFiles`); a step's pictures and a log picture are read from the picture cache, the
//! workspace or the inline data (`mightyImages`, `imageData`).
//!
//! The session record's older requests and the history block's words come from history.rs (`mightyLoadOlder` reads the next chunk).
//! What lives in later stages comes in empty (`Sources`): the Ouroboros dashboard (`NoDashboard`), the background output tails, the
//! custom agents' definitions and the guided style (W9).

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use mighty_composer::images as pictures;
use mighty_core::graph::GraphBlockSize;
use mighty_core::model::RunSession;
use mighty_graph::images::AgentImageLocation;
use mighty_graph::result_files::{self, StdFiles};
use mighty_graph::view::{MightyPane, MightyProjection, NoDashboard, Sources, project};
use mighty_graph::view_model;
use serde_json::{Value, json};

use crate::composer::{change, session};
use crate::host::{Host, Registry, View};
use crate::methods::{flag, number, text};
use crate::panes::{self, ensure};

/// The largest picture the page is sent (`imageData`, C#'s 8 MB).
const IMAGE_LIMIT: usize = 8 * 1024 * 1024;

/// The image path rule (`AgentImagePaths.Locate`) as mighty-graph reads it: mighty-composer's reading of the file system.
pub fn locate(source: &str, root: Option<&str>) -> AgentImageLocation {
    let temporary = pictures::temporary_roots();
    match pictures::locate(source, root.map(Path::new), Some(&temporary)) {
        pictures::AgentImageLocation::File { path, root } => {
            AgentImageLocation::File { path: path.to_string_lossy().into_owned(), root: root.to_string_lossy().into_owned() }
        }
        pictures::AgentImageLocation::Inline { media_type, base64 } => AgentImageLocation::Inline { media_type, base64 },
        pictures::AgentImageLocation::Remote { url, .. } => AgentImageLocation::Remote { url },
        pictures::AgentImageLocation::Refused => AgentImageLocation::Refused,
    }
}

/// `LocalPath(Workspace)`: the pane's folder on this computer; none for a remote workspace's pane.
fn root_of(view: &View, pane: &RunSession) -> Option<String> {
    view.snapshot.workspace(pane.workspace_id()).filter(|w| w.remote.is_none()).map(|w| w.path().to_string())
}

/// C# `WebImageUrl`: a picture as a data URL the page's policy allows (an SVG drawn to PNG, files.rs), or none (nothing read, more
/// than 8 MB, a kind the page does not draw).
fn image_url(data: Option<Vec<u8>>, media_type: &str) -> Option<String> {
    crate::files::web_image_url(data.filter(|d| !d.is_empty() && d.len() <= IMAGE_LIMIT), media_type)
}

/// `WebMightyModel(session)`: the pane's projection with what this host has of its sources.
fn projection(host: &Host, view: &mut View, pane: &Arc<RunSession>) -> Option<MightyProjection> {
    let id = pane.id().to_string();
    let root = root_of(view, pane);
    let requests = panes::requests(host, &id);
    let pending_plan =
        mighty_composer::cards::card_requests(&requests, false).first().is_some_and(|r| r.can_answer_plan && r.plan().is_some());
    let catalog: Vec<_> =
        host.runs().map(|r| r.catalog_of(pane.provider()).models.iter().map(|m| m.label_option()).collect()).unwrap_or_default();
    let tails = HashMap::new();
    let definitions = HashMap::new();
    let locator = locate;
    // The session record's older requests and the history block's words (history.rs, C# `GraphHistoryRuns`).
    let (older, history) = crate::history::older(host, view, pane);
    let sources = Sources {
        older: &older,
        history,
        pending_plan,
        executions: &[],
        dashboard: &NoDashboard,
        root: root.as_deref(),
        images: &locator,
        tails: &tails,
        definitions: &definitions,
        catalog: Some(&catalog),
        style: None,
        animations: true,
        zone: None,
    };
    let ui = view.panes.get_mut(&id)?;
    Some(project(pane, &mut ui.mighty, &sources, &host.locale))
}

/// One 120 ms tick of the Mighty pushes (C# `FlushWebMighty`).
pub fn flush(host: &Arc<Host>) {
    let mut view = host.view();
    if !view.ready || view.mighty.shown().next().is_none() {
        return;
    }
    let sessions = view.snapshot.sessions().to_vec();
    for pane in view.mighty.due(&sessions) {
        let id = pane.id().to_string();
        if !view.panes.contains_key(&id) {
            continue;
        }
        let Some(model) = projection(host, &mut view, &pane) else { continue };
        if let Some(message) = view.mighty.message(&id, &model) {
            host.push(&view, "mighty", message.to_string());
        }
        // LookThroughResultFiles: the files a finished result names, read off the page's thread.
        let lookups = view.panes.get_mut(&id).map(|u| u.mighty.take_result_file_lookups()).unwrap_or_default();
        let root = root_of(&view, &pane);
        for lookup in lookups {
            let (host, id, root) = (host.clone(), id.clone(), root.clone());
            tauri::async_runtime::spawn_blocking(move || {
                let texts = view_model::result_texts(&lookup.run);
                let files = result_files::files_in(&texts, root.as_deref(), &StdFiles { ignores_case: cfg!(windows) });
                let mut view = host.view();
                if let Some(ui) = view.panes.get_mut(&id) {
                    ui.mighty.found_result_files(lookup.run.id(), lookup.key, files);
                }
                view.mighty.dirty(&id);
            });
        }
    }
}

/// A page method changed what the pane's projection reads (`MightyDirty`).
fn dirty(view: &mut View, id: &str) {
    view.mighty.dirty(id);
}

pub fn register(methods: &mut Registry) {
    methods.add("mightyVisible", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        if flag(&args, "visible") {
            let id = ensure(&mut view, text(&args, "paneId"))?;
            view.mighty.visible(&id, true);
            panes::ui(&mut view, &id)?.mighty.set_shown(true);
        } else if let Some(id) = text(&args, "paneId") {
            view.mighty.visible(id, false);
            if let Some(ui) = view.panes.get_mut(id) {
                ui.mighty.set_shown(false);
            }
        }
        Ok(Value::Null)
    });
    methods.add("mightySetPresentation", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, text(&args, "paneId"))?;
        let mode = text(&args, "mode").unwrap_or("diagram").to_string();
        change(&mut view, &id, |p| MightyPane::set_presentation(p, &mode))?;
        dirty(&mut view, &id);
        Ok(Value::Null)
    });
    methods.add("mightySaveSize", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, text(&args, "paneId"))?;
        let pane = session(&view, &id)?;
        let size = match (number(&args, "width"), number(&args, "height")) {
            (Some(width), Some(height)) => Some(GraphBlockSize { width, height }),
            _ => None,
        };
        let node = text(&args, "nodeId").unwrap_or("").to_string();
        let target = text(&args, "target").unwrap_or("block").to_string();
        let older = crate::history::loaded(&host, &pane);
        let next = panes::ui(&mut view, &id)?.mighty.save_size(&pane, &node, &target, size, &older);
        if let Some(next) = next {
            change(&mut view, &id, |_| next)?;
        }
        dirty(&mut view, &id);
        Ok(Value::Null)
    });
    methods.add("mightyExpand", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, text(&args, "paneId"))?;
        let pane = session(&view, &id)?;
        let next = panes::ui(&mut view, &id)?.mighty.expand(&pane, text(&args, "nodeId").unwrap_or(""), flag(&args, "expanded"));
        change(&mut view, &id, |_| next)?;
        dirty(&mut view, &id);
        Ok(Value::Null)
    });
    methods.add("mightyResultFiles", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, text(&args, "paneId"))?;
        panes::ui(&mut view, &id)?.mighty.result_files(text(&args, "runId"));
        dirty(&mut view, &id);
        Ok(Value::Null)
    });
    // LoadOlderGraphHistory: the next older chunk of the session record, read off the page's thread (history.rs).
    methods.add("mightyLoadOlder", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, text(&args, "paneId"))?;
        crate::history::load_older(&host, &mut view, &id);
        dirty(&mut view, &id);
        Ok(Value::Null)
    });
    methods.add("mightyImages", |host: Arc<Host>, args| async move {
        let (pane, root) = {
            let mut view = host.view();
            let id = ensure(&mut view, text(&args, "paneId"))?;
            let pane = session(&view, &id)?;
            let root = root_of(&view, &pane);
            (pane, root)
        };
        let locator = locate;
        let older = crate::history::loaded(&host, &pane);
        let Some(page) = MightyPane::images(
            &pane,
            &older,
            text(&args, "runId").unwrap_or(""),
            text(&args, "step").unwrap_or(""),
            flag(&args, "expanded"),
            root.as_deref(),
            &locator,
        ) else {
            return Ok(Value::Null);
        };
        let cache = host.engine()?.images.clone();
        let tiles = tauri::async_runtime::spawn_blocking(move || {
            page.shown
                .iter()
                .map(|item| {
                    let (data, kind) = match (&item.stored, &item.located) {
                        (Some(stored), _) => (cache.data(stored), stored.media_type.clone()),
                        (None, Some(AgentImageLocation::File { path, root })) => (
                            pictures::read_located(&PathBuf::from(path), &PathBuf::from(root)).ok(),
                            pictures::media_type_for_file_name(path).map(str::to_string),
                        ),
                        (None, Some(AgentImageLocation::Inline { media_type, base64 })) => {
                            (pictures::decode_base64(base64).ok(), Some(media_type.clone()))
                        }
                        _ => (None, None),
                    };
                    let url = image_url(data, kind.as_deref().unwrap_or("image/png"));
                    let mut tile = json!({"id": item.id, "alt": item.alt});
                    if let Some(url) = url {
                        tile["url"] = Value::from(url);
                    }
                    tile
                })
                .collect::<Vec<_>>()
        })
        .await
        .unwrap_or_default();
        Ok(json!({"count": page.count, "tiles": tiles}))
    });
    methods.add("mightyTimelineFlip", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, text(&args, "paneId"))?;
        panes::ui(&mut view, &id)?.mighty.timeline_flip(text(&args, "runId").unwrap_or(""));
        dirty(&mut view, &id);
        Ok(Value::Null)
    });
    methods.add("mightyLogCollapsed", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, text(&args, "paneId"))?;
        let collapsed = flag(&args, "collapsed");
        change(&mut view, &id, |p| MightyPane::log_collapsed(p, collapsed))?;
        dirty(&mut view, &id);
        Ok(Value::Null)
    });
    // OpenOuroborosDashboard is the dashboard's (not ported): the execution the block names is known, nothing opens yet.
    methods.add("mightyOpenDashboard", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, text(&args, "paneId"))?;
        let _execution = panes::ui(&mut view, &id)?.mighty.open_dashboard(text(&args, "key").unwrap_or("")).cloned();
        dirty(&mut view, &id);
        Ok(Value::Null)
    });
    // A pane's log picture by its hash, as a data URL (C# `imageData`); an SVG drawn to PNG as C# draws it.
    methods.add("imageData", |host: Arc<Host>, args| async move {
        let Some(hash) = text(&args, "hash").map(str::to_string) else { return Ok(Value::Null) };
        let image = {
            let view = host.view();
            let Some(pane) = text(&args, "paneId").and_then(|id| view.snapshot.session(id)).cloned() else { return Ok(Value::Null) };
            pane.logs().iter().flat_map(|e| e.images.iter().flatten().flatten()).find(|i| i.hash.as_deref() == Some(hash.as_str())).cloned()
        };
        let Some(image) = image else { return Ok(Value::Null) };
        let cache = host.engine()?.images.clone();
        let kind = image.media_type.clone().unwrap_or_else(|| "image/png".into());
        let data = tauri::async_runtime::spawn_blocking(move || cache.data(&image)).await.ok().flatten();
        Ok(image_url(data, &kind).map_or(Value::Null, Value::from))
    });
}
