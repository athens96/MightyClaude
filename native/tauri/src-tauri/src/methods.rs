//! The page methods this host implements, each the C# handler's rule (MainWindow.WebShell.cs, MainWindow.WebShell.Shell.cs,
//! MainWindow.Layout.cs, MainWindow.Navigation.cs, MainWindow.Sidebar.cs, MainWindow.SidebarToggle.cs). Every change goes through
//! `View::update` (C# `DesktopService.UpdateAsync`: normalized, then saved); the flush after the call sends it to the page.

use std::sync::Arc;

use mighty_core::AppSnapshot;
use mighty_core::layout;
use mighty_core::model::PaneLayoutNode;
use mighty_core::shell::{effective_layout, open_disclosure, toggle_disclosure};
use mighty_core::{locale, normalize, wire};
use serde_json::{Value, json};

use crate::host::{Host, Registry, Reply, View};

fn text<'a>(args: &'a Value, name: &str) -> Option<&'a str> {
    args.get(name).and_then(Value::as_str)
}

/// C# `ArgNumber`: a JSON number.
fn number(args: &Value, name: &str) -> Option<f64> {
    args.get(name).filter(|v| v.is_number()).and_then(Value::as_f64)
}

fn flag(args: &Value, name: &str) -> bool {
    args.get(name) == Some(&Value::Bool(true))
}

/// C# `SaveLayout`: the workspace's tree, or none.
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

/// C# `SaveLayoutSelection`.
fn save_selection(state: &mut AppSnapshot, workspace: &str, id: &str) {
    state.pane_layout_active_session_ids.get_or_insert_with(Default::default).insert(workspace.to_string(), Some(id.to_string()));
}

/// C# `SaveLayoutMode`.
fn save_mode(state: &mut AppSnapshot, workspace: &str, mode: &str) {
    state.pane_layout_modes.get_or_insert_with(Default::default).insert(workspace.to_string(), Some(mode.to_string()));
}

/// C# `SelectLayoutSession`: the pane's group shows it, and it becomes the active pane of its workspace, which becomes the active one.
fn select_session(view: &mut View, id: &str) -> Result<(), String> {
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

/// C# `ActivatePane`: a pane of the active workspace becomes the active one (a click inside it).
fn activate_pane(view: &mut View, id: &str) -> Result<(), String> {
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
fn select_workspace(view: &mut View, id: &str) -> Result<(), String> {
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

/// C# `ApplyLayoutPreset`: focus and custom keep the workspace's tree; the other presets lay its panes out again.
fn apply_layout_preset(view: &mut View, preset: &str) -> Result<(), String> {
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

/// C# `ResizeSplit` (and `ResetSplit`, at .5): the split's share saved, the layout made custom.
fn resize_split(view: &mut View, workspace: &str, split: &str, ratio: f64) -> Result<(), String> {
    let current = effective_layout(&view.snapshot, workspace, &mut view.layout_defaults);
    view.update(|state| {
        if let Some(current) = current {
            save_layout(state, workspace, Some(layout::resize(&current, split, ratio)));
            save_mode(state, workspace, "custom");
        }
    })
}

/// C# `DockSession`: a tab of the active workspace moved into a group or beside it, the layout made custom, the tab the active pane.
fn dock_session(view: &mut View, id: &str, workspace: &str, group: &str, edge: &str, index: Option<i64>) -> Result<(), String> {
    let applies = view.snapshot.active_workspace_id.as_deref() == Some(workspace)
        && view.snapshot.sessions().iter().any(|s| s.id() == id && s.workspace_id() == workspace);
    let current = if applies { effective_layout(&view.snapshot, workspace, &mut view.layout_defaults) } else { None };
    let current = current.filter(|node| layout::groups(node).iter().any(|g| g.id() == group));
    view.update(|state| {
        if let Some(current) = current {
            save_layout(state, workspace, Some(layout::move_tab(&current, id, group, edge, index)));
            save_mode(state, workspace, "custom");
            save_selection(state, workspace, id);
            state.active_session_id = Some(id.to_string());
        }
    })
}

/// C# `RenameSupport.Messages` and `IsValid` (RenameSupport.cs): at most 120 text elements and no control character.
fn rename_check(locale: &locale::Locale, text: &str) -> Value {
    let name = text.trim();
    let too_long = wire::text_elements(name, 120).1;
    let control = name.chars().any(|c| (c as u32) < 0x20 || (0x7f..=0x9f).contains(&(c as u32)));
    let mut messages = vec![];
    if too_long {
        messages.push(locale.get("pane.rename.tooLong"));
    }
    if control {
        messages.push(locale.get("pane.rename.noNewlines"));
    }
    json!({"valid": !name.is_empty() && !too_long && !control, "messages": messages})
}

fn change(host: &Host, edit: impl FnOnce(&mut View) -> Result<(), String>) -> Reply {
    edit(&mut host.view()).map(|_| Value::Null)
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
        change(&host, |view| match text(&args, "id").filter(|id| wire::identifier(id)) {
            Some(id) => select_session(view, id),
            None => Ok(()),
        })
    });
    methods.add("activatePane", |host: Arc<Host>, args| async move {
        change(&host, |view| match text(&args, "id") {
            Some(id) => activate_pane(view, id),
            None => Ok(()),
        })
    });
    methods.add("selectWorkspace", |host: Arc<Host>, args| async move {
        change(&host, |view| match text(&args, "id") {
            Some(id) => select_workspace(view, id),
            None => Ok(()),
        })
    });
    methods.add("setTheme", |host: Arc<Host>, args| async move {
        let theme = if text(&args, "theme") == Some("light") { "light" } else { "dark" };
        change(&host, |view| view.update(|state| state.theme = Some(theme.into())))
    });
    methods.add("setSidebarCollapsed", |host: Arc<Host>, args| async move {
        let collapsed = flag(&args, "collapsed");
        change(&host, |view| {
            if view.snapshot.sidebar_collapsed == collapsed { Ok(()) } else { view.update(|state| state.sidebar_collapsed = collapsed) }
        })
    });
    methods.add("setSidebarWidth", |host: Arc<Host>, args| async move {
        change(&host, |view| match number(&args, "width").filter(|w| !w.is_nan()) {
            Some(width) => view.update(|state| state.sidebar_width = width.clamp(normalize::SIDEBAR_MIN, normalize::SIDEBAR_MAX)),
            None => Ok(()),
        })
    });
    methods.add("toggleDisclosure", |host: Arc<Host>, args| async move {
        change(&host, |view| {
            let Some(id) = text(&args, "id") else { return Ok(()) };
            let list = toggle_disclosure(&view.snapshot, id);
            view.update(|state| {
                if let Some(list) = list {
                    state.expanded_workspace_ids = Some(list);
                }
            })
        })
    });
    methods.add("applyLayoutPreset", |host: Arc<Host>, args| async move {
        change(&host, |view| match text(&args, "preset").filter(|p| matches!(*p, "grid" | "columns" | "focus" | "tabs" | "custom")) {
            Some(preset) => apply_layout_preset(view, preset),
            None => Ok(()),
        })
    });
    methods.add("resizeSplit", |host: Arc<Host>, args| async move {
        change(&host, |view| match (text(&args, "workspaceId"), text(&args, "splitId"), number(&args, "ratio").filter(|r| !r.is_nan())) {
            (Some(workspace), Some(split), Some(ratio)) => resize_split(view, workspace, split, layout::clamp_ratio(ratio)),
            _ => Ok(()),
        })
    });
    methods.add("resetSplit", |host: Arc<Host>, args| async move {
        change(&host, |view| match (text(&args, "workspaceId"), text(&args, "splitId")) {
            (Some(workspace), Some(split)) => resize_split(view, workspace, split, 0.5),
            _ => Ok(()),
        })
    });
    methods.add("dockSession", |host: Arc<Host>, args| async move {
        change(&host, |view| {
            let (Some(id), Some(workspace), Some(group)) = (text(&args, "sessionId"), text(&args, "workspaceId"), text(&args, "groupId"))
            else {
                return Ok(());
            };
            let edge = text(&args, "edge").filter(|e| matches!(*e, "left" | "right" | "top" | "bottom")).unwrap_or("center");
            let index = number(&args, "index").filter(|at| *at >= 0.0).map(|at| at as i64);
            dock_session(view, id, workspace, group, edge, index)
        })
    });
    methods.add("showDashboard", |host: Arc<Host>, _| async move {
        host.view().shows_dashboard = true;
        Ok(Value::Null)
    });
    methods.add("hideDashboard", |host: Arc<Host>, _| async move {
        host.view().shows_dashboard = false;
        Ok(Value::Null)
    });
    methods.add("dismissError", |host: Arc<Host>, _| async move {
        host.view().error = None;
        Ok(Value::Null)
    });
    methods.add("renameCheck", |host: Arc<Host>, args| async move { Ok(rename_check(&host.locale, text(&args, "text").unwrap_or(""))) });
    crate::surface::register(&mut methods);
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

#[cfg(test)]
mod tests {
    use super::*;

    /// RenameSupport.cs:23 `Messages` and :14 `IsValid`.
    #[test]
    fn rename_check_follows_rename_support() {
        let en = locale::Locale::new("en");
        assert_eq!(rename_check(&en, "  ok  ")["valid"], json!(true));
        assert_eq!(rename_check(&en, "   ")["valid"], json!(false));
        assert_eq!(rename_check(&en, "   ")["messages"], json!([]));
        let both = rename_check(&en, &format!("{}\nb", "a".repeat(121)));
        assert_eq!(both["messages"], json!([en.get("pane.rename.tooLong"), en.get("pane.rename.noNewlines")]));
    }
}
