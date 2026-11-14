//! Completion notifications (C# MainWindow.CompletionNotification.cs: `InitNotifierAsync`, `HandleRunEventForNotification`,
//! `FocusSession`; `RunCompletionNotificationSmoke`), over mighty-core's `notifications`. Owner: W6-A.
//!
//! A run seen running that completes, with the setting on, shows one toast (`Ports::notifier`): the localized title over "{title} has
//! finished.". Registering with the OS writes the user's registry, so it happens only with `--allow-state-write` and never in a smoke;
//! without it the permission row reads "verification mode" and no toast shows. A click on a toast brings the window forward, pushes
//! `select{paneId}` and selects the pane.

use std::sync::{Arc, Mutex};

use mighty_core::locale::Locale;
use mighty_core::notifications::{self, Decision, SmokeOutcome};
use mighty_platform::Notification;
use mighty_run::RunEvent;
use tauri::Manager;

use crate::host::Host;

/// Whether the OS lets the app notify (C# `NotificationPermission`): `Verification` while the app may not register with the OS
/// (without `--allow-state-write`, and in a smoke), else allowed or needing the user's permission.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum NotificationPermission {
    #[default]
    Verification,
    Allowed,
    NeedPermission,
}

#[derive(Default)]
struct State {
    decision: Decision,
    permission: NotificationPermission,
}

static STATE: Mutex<Option<State>> = Mutex::new(None);

fn with<T>(f: impl FnOnce(&mut State) -> T) -> T {
    let mut state = STATE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    f(state.get_or_insert_with(State::default))
}

/// C# `InitNotifierAsync` (never in a smoke): registered with the OS when the app may write the user's settings, and a click on a
/// toast focuses its pane.
pub fn start(host: &Arc<Host>) {
    let permission = if host.may_change_machine() {
        match crate::platform::register_notifications() {
            Ok(()) => NotificationPermission::Allowed,
            Err(error) => {
                host.log.line(&format!("notifications: not registered: {error}"));
                NotificationPermission::NeedPermission
            }
        }
    } else {
        NotificationPermission::Verification
    };
    with(|state| state.permission = permission);
    if let Some(ports) = host.app.try_state::<crate::platform::Ports>() {
        let to = host.clone();
        ports.notifier.on_activated(Arc::new(move |pane| {
            if let Some(pane) = pane {
                focus(&to, &pane);
            }
        }));
    }
}

/// `notifications::permission`: what the companion box's permission row says.
pub fn permission(_host: &Host) -> NotificationPermission {
    with(|state| state.permission)
}

/// The permission row's words (C# `BuildCompanionSection`).
pub fn permission_text(locale: &Locale, permission: NotificationPermission) -> String {
    notifications::text(
        locale,
        match permission {
            NotificationPermission::Allowed => "StatusAllowed",
            NotificationPermission::NeedPermission => "StatusNeedPermission",
            NotificationPermission::Verification => "StatusVerificationMode",
        },
    )
}

/// C# `HandleRunEventForNotification` (the last thing the window does with a run event): a toast when the decision says so and the app
/// may notify; its body names the pane's title alone.
pub fn on_event(host: &Arc<Host>, event: &RunEvent) {
    if host.closing() || event.kind != "status" {
        return;
    }
    let (title, run_title) = {
        let view = host.view();
        let enabled = view.snapshot.completion_notifications_enabled;
        let title = with(|state| state.decision.process(&event.kind, event.status.as_deref(), &event.session_id, &view.snapshot, enabled));
        let run_title = view.snapshot.session(&event.session_id).map_or_else(|| event.session_id.clone(), |s| s.title().to_string());
        (title, run_title)
    };
    if title.is_none() || permission(host) != NotificationPermission::Allowed {
        return;
    }
    let Some(ports) = host.app.try_state::<crate::platform::Ports>() else { return };
    let notifier = ports.notifier.clone();
    let notification = Notification {
        title: notifications::text(&host.locale, "NotificationTitle"),
        body: notifications::body(&host.locale, &run_title),
        pane_id: Some(event.session_id.clone()),
    };
    let log = host.clone();
    tauri::async_runtime::spawn(async move {
        if let Err(error) = notifier.notify(notification).await {
            log.log.line(&format!("notifications: not shown: {error}"));
        }
    });
}

/// C# `FocusSession`: the pane's window forward, `select{paneId}` pushed, the pane selected.
pub fn focus(host: &Arc<Host>, pane: &str) {
    if host.view().snapshot.session(pane).is_none() {
        return;
    }
    if let Some(window) = host.app.get_webview_window(crate::host::WINDOW) {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
    }
    let mut view = host.view();
    let pane_json = serde_json::to_string(pane).unwrap_or_default();
    host.push(&view, "select", format!("{{\"kind\":\"select\",\"paneId\":{pane_json}}}"));
    if let Err(error) = crate::frame::select_session(&mut view, pane) {
        view.error = Some(error);
    }
}

/// The live smoke's `completionNotification` (C# `RunCompletionNotificationSmoke`): a smoke never registers with the OS (it would write
/// the user's registry), so it is unsupported there and skipped, as on CI's runners.
pub async fn smoke(host: &Arc<Host>) -> SmokeOutcome {
    let supported = host.may_change_machine();
    let outcome = notifications::smoke_run(
        supported,
        async || crate::platform::register_notifications().err(),
        async |title: &str, id: &str| -> Result<(), String> {
            let Some(ports) = host.app.try_state::<crate::platform::Ports>() else { return Ok(()) };
            let notification =
                Notification { title: title.into(), body: notifications::body(&host.locale, title), pane_id: Some(id.into()) };
            ports.notifier.notify(notification).await.map_err(|e| e.to_string())
        },
    )
    .await;
    outcome.unwrap_or_else(|error| SmokeOutcome::skipped(Some(&error)))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_permission_row_reads_each_state() {
        let ko = Locale::new("ko");
        assert_eq!(permission_text(&ko, NotificationPermission::Verification), "검증 모드");
        assert_eq!(permission_text(&ko, NotificationPermission::Allowed), "허용됨");
        assert_eq!(permission_text(&ko, NotificationPermission::NeedPermission), "권한 필요");
    }
}
