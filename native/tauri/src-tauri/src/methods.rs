//! The page methods this skeleton implements, each the C# handler's rule (MainWindow.WebShell.cs, MainWindow.WebShell.Shell.cs,
//! MainWindow.Layout.cs, MainWindow.Navigation.cs, MainWindow.Sidebar.cs, MainWindow.SidebarToggle.cs). Every change is made to
//! the in-memory state only; the flush after the call sends it to the page.

use std::sync::Arc;

use mighty_core::AppSnapshot;
use mighty_core::layout::{self, Node};
use mighty_core::shell::{effective_layout, toggle_disclosure};
use mighty_core::wire::{self, Lenient};
use mighty_core::{locale, normalize};
use serde_json::{Value, json};

use crate::host::{Host, Registry, Reply, View};

fn text<'a>(args: &'a Value, name: &str) -> Option<&'a str> {
    args.get(name).and_then(Value::as_str)
}

fn number(args: &Value, name: &str) -> Option<f64> {
    args.get(name).and_then(Value::as_f64)
}

fn flag(args: &Value, name: &str) -> bool {
    args.get(name) == Some(&Value::Bool(true))
}

/// C# `SaveLayout`: the workspace's tree, or none.
fn save_layout(state: &mut AppSnapshot, workspace: &str, node: Option<&Node>) {
    let layouts = state.pane_layouts.get_or_insert_with(Default::default);
    match node {
        Some(node) => {
            layouts.insert(workspace.to_string(), node.to_raw());
        }
        None => {
            layouts.remove(workspace);
        }
    }
}

/// C# `SaveLayoutSelection`.
fn save_selection(state: &mut AppSnapshot, workspace: &str, id: &str) {
    state.pane_layout_active_session_ids.get_or_insert_with(Default::default).insert(workspace.to_string(), id.to_string());
}

/// C# `SaveLayoutMode`.
fn save_mode(state: &mut AppSnapshot, workspace: &str, mode: &str) {
    state.pane_layout_modes.get_or_insert_with(Default::default).insert(workspace.to_string(), mode.to_string());
}

/// C# `SelectLayoutSession`: the pane's group shows it, and it becomes the active pane of its workspace, which becomes the active one.
fn select_session(view: &mut View, id: &str) {
    let Some(workspace) = view.snapshot.session(id).map(|s| s.workspace_id().to_string()) else { return };
    let node = effective_layout(&view.snapshot, &workspace, &mut view.layout_defaults).map(|n| layout::select(&n, id));
    view.update(|state| {
        save_layout(state, &workspace, node.as_ref());
        save_selection(state, &workspace, id);
        state.active_workspace_id = Some(workspace.clone());
        state.active_session_id = Some(id.to_string());
    });
}

/// C# `ActivatePane`: a pane of the active workspace becomes the active one (a click inside it).
fn activate_pane(view: &mut View, id: &str) {
    if view.snapshot.active_session_id.as_deref() == Some(id) {
        return;
    }
    let Some(workspace) = view.snapshot.session(id).map(|s| s.workspace_id().to_string()) else { return };
    if view.snapshot.active_workspace_id.as_deref() != Some(workspace.as_str()) {
        return;
    }
    view.update(|state| {
        save_selection(state, &workspace, id);
        state.active_session_id = Some(id.to_string());
    });
}

/// C# `SelectWorkspace`: opened in the sidebar and made active, with the pane it last showed (else its first group's).
fn select_workspace(view: &mut View, id: &str) {
    if view.snapshot.workspace(id).is_none() {
        return;
    }
    let preferred = view.snapshot.pane_layout_active_session_ids.as_ref().and_then(|m| m.get(id)).cloned();
    let selected = view
        .snapshot
        .sessions
        .iter()
        .find(|s| s.workspace_id() == id && Some(s.id()) == preferred.as_deref())
        .map(|s| s.id().to_string())
        .or_else(|| {
            effective_layout(&view.snapshot, id, &mut view.layout_defaults)
                .and_then(|tree| layout::groups(&tree).first().and_then(|g| g.selected_session_id.clone()))
        });
    let mut expanded = mighty_core::shell::expanded(&view.snapshot).into_iter().collect::<Vec<_>>();
    if !expanded.iter().any(|e| e == id) {
        expanded.push(id.to_string());
    }
    expanded.sort();
    view.update(|state| {
        state.expanded_workspace_ids = Some(Lenient::Valid(expanded));
        state.active_workspace_id = Some(id.to_string());
        state.active_session_id = selected;
    });
}

/// C# `ApplyLayoutPreset`: focus and custom keep the workspace's tree; the other presets lay its panes out again.
fn apply_layout_preset(view: &mut View, preset: &str) {
    let Some(workspace) = view.snapshot.active_workspace_id.clone() else {
        view.update(|state| state.layout = Some(preset.to_string()));
        return;
    };
    let node = if matches!(preset, "custom" | "focus") {
        effective_layout(&view.snapshot, &workspace, &mut view.layout_defaults)
    } else {
        layout::preset(&view.snapshot.pane_ids(&workspace), preset, view.snapshot.active_session_id.as_deref())
    };
    view.update(|state| {
        save_layout(state, &workspace, node.as_ref());
        save_mode(state, &workspace, preset);
    });
}

fn change(host: &Host, edit: impl FnOnce(&mut View)) -> Reply {
    edit(&mut host.view());
    Ok(Value::Null)
}

pub fn registry() -> Registry {
    let mut methods = Registry::default();
    // The page holds every catalogue; the host knows what "system" reads on this computer (C# Locale.ChosenLanguage).
    methods.add("getLocale", |host: Arc<Host>, _| async move {
        let preference = host.view().snapshot.language_preference().to_string();
        Ok(json!({"preference": preference, "language": host.locale.language()}))
    });
    methods.add("ready", |host: Arc<Host>, _| async move {
        host.ready();
        Ok(Value::Null)
    });
    methods.add("shellState", |host: Arc<Host>, _| async move {
        let mut view = host.view();
        Ok(serde_json::to_value(host.shell_model(&mut view)).unwrap_or(Value::Null))
    });
    methods.add("log", |host: Arc<Host>, args| async move {
        let level = text(&args, "level").unwrap_or("info");
        host.log.line(&format!("web {level}: {}", wire::clean(text(&args, "message").unwrap_or(""), 2048)));
        Ok(Value::Null)
    });
    methods.add("selectSession", |host: Arc<Host>, args| async move {
        change(&host, |view| {
            if let Some(id) = text(&args, "id").filter(|id| wire::identifier(id)) {
                select_session(view, id);
            }
        })
    });
    methods.add("activatePane", |host: Arc<Host>, args| async move {
        change(&host, |view| {
            if let Some(id) = text(&args, "id") {
                activate_pane(view, id);
            }
        })
    });
    methods.add("selectWorkspace", |host: Arc<Host>, args| async move {
        change(&host, |view| {
            if let Some(id) = text(&args, "id") {
                select_workspace(view, id);
            }
        })
    });
    methods.add("setTheme", |host: Arc<Host>, args| async move {
        let theme = if text(&args, "theme") == Some("light") { "light" } else { "dark" };
        change(&host, |view| view.update(|state| state.theme = Some(theme.into())))
    });
    methods.add("setSidebarCollapsed", |host: Arc<Host>, args| async move {
        let collapsed = flag(&args, "collapsed");
        change(&host, |view| {
            if view.snapshot.sidebar_collapsed != Some(collapsed) {
                view.update(|state| state.sidebar_collapsed = Some(collapsed));
            }
        })
    });
    methods.add("setSidebarWidth", |host: Arc<Host>, args| async move {
        change(&host, |view| {
            if let Some(width) = number(&args, "width").filter(|w| !w.is_nan()) {
                view.update(|state| state.sidebar_width = Some(width.clamp(normalize::SIDEBAR_MIN, normalize::SIDEBAR_MAX)));
            }
        })
    });
    methods.add("toggleDisclosure", |host: Arc<Host>, args| async move {
        change(&host, |view| {
            if let Some(list) = text(&args, "id").and_then(|id| toggle_disclosure(&view.snapshot, id)) {
                view.update(|state| state.expanded_workspace_ids = Some(Lenient::Valid(list)));
            }
        })
    });
    methods.add("applyLayoutPreset", |host: Arc<Host>, args| async move {
        change(&host, |view| {
            if let Some(preset) = text(&args, "preset").filter(|p| matches!(*p, "grid" | "columns" | "focus" | "tabs" | "custom")) {
                apply_layout_preset(view, preset);
            }
        })
    });
    methods
}

/// The language "system" reads on this computer: the user's Windows display languages, most preferred first.
pub fn os_languages() -> Vec<String> {
    sys_locale::get_locales().collect()
}

/// The language the page reads (C# `Locale.ChosenLanguage` with the saved preference).
pub fn chosen_language(preference: &str) -> &'static str {
    locale::chosen_language(preference, &os_languages())
}
