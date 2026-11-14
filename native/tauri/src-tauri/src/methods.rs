//! The page methods this host implements, each the C# handler's rule (MainWindow.WebShell.cs, MainWindow.WebShell.Shell.cs,
//! MainWindow.Layout.cs, MainWindow.Navigation.cs, MainWindow.Sidebar.cs, MainWindow.SidebarToggle.cs). Every change goes through
//! `View::update` (C# `DesktopService.UpdateAsync`: normalized, then saved); the flush after the call sends it to the page.

use std::sync::Arc;

use mighty_core::add_pane::{save_layout, save_mode, save_selection};
use mighty_core::layout;
use mighty_core::shell::{effective_layout, toggle_disclosure};
use mighty_core::{locale, normalize, wire};
use serde_json::{Value, json};

use crate::frame::{activate_pane, apply_layout_preset, select_session, select_workspace};
use crate::host::{Host, Registry, Reply, View};

pub fn text<'a>(args: &'a Value, name: &str) -> Option<&'a str> {
    args.get(name).and_then(Value::as_str)
}

/// C# `ArgNumber`: a JSON number.
pub fn number(args: &Value, name: &str) -> Option<f64> {
    args.get(name).filter(|v| v.is_number()).and_then(Value::as_f64)
}

pub fn flag(args: &Value, name: &str) -> bool {
    args.get(name) == Some(&Value::Bool(true))
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
        change(&host, |view| view.update(|state| state.theme = Some(theme.into())))?;
        // C# `Retheme`: the terminals take their colours again.
        crate::terminal::restyle(&host);
        Ok(Value::Null)
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
    crate::composer::register(&mut methods);
    crate::cards::register(&mut methods);
    crate::mighty::register(&mut methods);
    crate::terminal::register(&mut methods);
    crate::files::register(&mut methods);
    crate::history::register(&mut methods);
    crate::frame::register(&mut methods);
    // W6: the native forms (native.rs) and Settings with the guide (settings/mod.rs).
    crate::native::register(&mut methods);
    crate::settings::register(&mut methods);
    methods.add("openUrl", |_host: Arc<Host>, args| async move {
        // C# `openUrl`: an http, https or mailto address, to the user's default handler; anything else does nothing.
        if let Some(url) = text(&args, "url").and_then(openable_url) {
            crate::platform::open_url(&url)?;
        }
        Ok(Value::Null)
    });
    methods
}

/// C# `Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" or "mailto"`, handed on as
/// `parsed.AbsoluteUri`: the address parsed, of one of those schemes, as its normalized text (what the shell is given is what was
/// checked). An address holding a control character is refused whole: the parser would drop a tab or a line break inside it, and
/// trim them around it, so the text checked would not be the text written.
pub(crate) fn openable_url(url: &str) -> Option<String> {
    if url.chars().any(char::is_control) {
        return None;
    }
    let parsed = tauri::Url::parse(url).ok()?;
    matches!(parsed.scheme(), "http" | "https" | "mailto").then(|| parsed.as_str().to_string())
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

    /// C# `openUrl`: http, https and mailto addresses only, handed on normalized; nothing with a control character in it.
    #[test]
    fn only_web_and_mail_addresses_open() {
        assert_eq!(openable_url("https://example.com/a b").as_deref(), Some("https://example.com/a%20b"));
        assert_eq!(openable_url("HTTP://Example.COM").as_deref(), Some("http://example.com/"));
        assert_eq!(openable_url("mailto:someone@example.com").as_deref(), Some("mailto:someone@example.com"));
        for refused in [
            "file:///C:/Windows/System32/calc.exe",
            "javascript:alert(1)",
            "ms-settings:privacy",
            "C:\\Windows\\notepad.exe",
            "\\\\server\\share",
            "example.com",
            "https://example.com/\nfile:///C:/x",
            "\thttps://example.com",
            "https://example.com/\u{7}",
            "",
        ] {
            assert_eq!(openable_url(refused), None, "{refused:?}");
        }
    }

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
