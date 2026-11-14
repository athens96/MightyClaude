//! Completion notifications (C# `CompletionNotificationDecision`, CompletionNotifications.cs; `CompletionNotificationStrings`;
//! `CompletionNotificationSmoke`, `CompletionNotificationSmokeOutcome`): when a run that was seen running completes, one notification
//! names it; its words in the app's language; the smoke's outcome. Owner: W6-A.

use std::collections::HashSet;

use crate::locale::Locale;
use crate::model::AppSnapshot;

/// Decides which run endings notify (C# `CompletionNotificationDecision`).
#[derive(Debug, Default)]
pub struct Decision {
    active: HashSet<String>,
}

impl Decision {
    /// `Process`: a status event of pane `session_id`. `running` remembers it; `error` and `stopped` forget it; `completed` forgets it and,
    /// when it was running and the setting is on, answers what the notification names: "workspace · title", or the title alone.
    pub fn process(&mut self, kind: &str, status: Option<&str>, session_id: &str, snapshot: &AppSnapshot, enabled: bool) -> Option<String> {
        if kind != "status" {
            return None;
        }
        match status? {
            "running" => {
                self.active.insert(session_id.to_string());
                None
            }
            "error" | "stopped" => {
                self.active.remove(session_id);
                None
            }
            "completed" => {
                let was_running = self.active.remove(session_id);
                if !was_running || !enabled {
                    return None;
                }
                let session = snapshot.session(session_id);
                let title = session.map_or_else(|| session_id.to_string(), |s| s.title().to_string());
                let workspace = session.and_then(|s| snapshot.workspace(s.workspace_id())).map(|w| w.name().to_string());
                Some(match workspace {
                    Some(workspace) => format!("{workspace} · {title}"),
                    None => title,
                })
            }
            _ => None,
        }
    }
}

/// `CompletionNotificationStrings`: each property's key, in C#'s order (read afresh each time, never cached).
pub const STRINGS: [(&str, &str); 8] = [
    ("NotificationTitle", "windows.notifications.notificationTitle"),
    ("NotificationBodyTemplate", "windows.notifications.notificationBodyTemplate"),
    ("ToggleLabel", "windows.notifications.toggleLabel"),
    ("StatusAllowed", "windows.notifications.statusAllowed"),
    ("StatusDenied", "windows.notifications.statusDenied"),
    ("StatusNeedPermission", "windows.notifications.statusNeedPermission"),
    ("StatusVerificationMode", "windows.notifications.statusVerificationMode"),
    ("SettingsButton", "windows.notifications.settingsButton"),
];

/// One property's words.
pub fn text(locale: &Locale, property: &str) -> String {
    STRINGS.iter().find(|(name, _)| *name == property).map(|(_, key)| locale.get(key)).unwrap_or_default()
}

/// The toast's body for a run (`NotificationBodyTemplate` with `{title}`).
pub fn body(locale: &Locale, run_title: &str) -> String {
    text(locale, "NotificationBodyTemplate").replace("{title}", run_title)
}

/// The smoke's fixture send (`FixtureTitle`, `FixtureSessionId`) and its reasons.
pub const FIXTURE_TITLE: &str = "Smoke fixture";
pub const FIXTURE_SESSION_ID: &str = "smoke-fixture-session";
pub const UNSUPPORTED_REASON: &str = "IsSupported false";
pub const UNKNOWN_REASON: &str = "unknown reason";

/// What the smoke records under `completionNotification` (C# `CompletionNotificationSmokeOutcome`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SmokeOutcome {
    Sent,
    Skipped(String),
}

impl SmokeOutcome {
    /// `Skipped(reason)`: the reason cleaned (`Wire.Clean`, 200 units) and trimmed, else "unknown reason".
    pub fn skipped(reason: Option<&str>) -> SmokeOutcome {
        let cleaned = crate::wire::clean(reason.unwrap_or_default(), 200).trim().to_string();
        SmokeOutcome::Skipped(if cleaned.is_empty() { UNKNOWN_REASON.to_string() } else { cleaned })
    }

    /// Its JSON as C# writes it: `{"status":"sent"}` or `{"status":"skipped","reason":…}`.
    pub fn to_json(&self) -> String {
        let mut out = crate::json::JsonOut::new();
        out.begin_object();
        match self {
            SmokeOutcome::Sent => out.prop_str("status", Some("sent")),
            SmokeOutcome::Skipped(reason) => {
                out.prop_str("status", Some("skipped"));
                out.prop_str("reason", Some(reason));
            }
        }
        out.end_object();
        out.finish()
    }
}

/// `CompletionNotificationSmoke.RunAsync`: unsupported skips without registering; a registration that answers a reason skips without
/// sending; else one fixture send, whose failure is the smoke's (the error comes back).
pub async fn smoke_run<R, S, E>(supported: bool, register: R, send: S) -> Result<SmokeOutcome, E>
where
    R: AsyncFnOnce() -> Option<String>,
    S: AsyncFnOnce(&str, &str) -> Result<(), E>,
{
    if !supported {
        return Ok(SmokeOutcome::skipped(Some(UNSUPPORTED_REASON)));
    }
    if let Some(reason) = register().await {
        return Ok(SmokeOutcome::skipped(Some(&reason)));
    }
    send(FIXTURE_TITLE, FIXTURE_SESSION_ID).await?;
    Ok(SmokeOutcome::Sent)
}

#[cfg(test)]
mod tests {
    use std::cell::RefCell;

    use super::*;
    use crate::Wire;

    /// C#'s `MakeSnapshot(ws, title)`: a workspace named `ws` and a pane titled `title` in it.
    fn snapshot(workspace: &str, title: &str) -> (AppSnapshot, String) {
        let state = serde_json::json!({
            "version": 1,
            "workspaces": [{"id": "w1", "name": workspace, "path": "C:\\w"}],
            "sessions": [{"id": "s1", "workspaceId": "w1", "title": title}],
        });
        (AppSnapshot::read(&state).unwrap(), "s1".into())
    }

    fn run(decision: &mut Decision, state: &AppSnapshot, id: &str, status: &str, enabled: bool) -> Option<String> {
        decision.process("status", Some(status), id, state, enabled)
    }

    /// Verification.cs:1125 "completion notification fires once for running then completed".
    #[test]
    fn fires_once_after_running() {
        let (state, id) = snapshot("Workspace", "My Run");
        let mut d = Decision::default();
        assert_eq!(run(&mut d, &state, &id, "running", true), None);
        assert!(run(&mut d, &state, &id, "completed", true).is_some());
    }

    /// Verification.cs:1126 "completion notification title includes workspace name".
    #[test]
    fn title_includes_workspace_name() {
        let (state, id) = snapshot("My Project", "My Session");
        let mut d = Decision::default();
        run(&mut d, &state, &id, "running", true);
        assert_eq!(run(&mut d, &state, &id, "completed", true).as_deref(), Some("My Project · My Session"));
    }

    /// Verification.cs:1127 "completion notification title is session alone without workspace".
    #[test]
    fn title_is_session_alone_without_workspace() {
        let state =
            AppSnapshot::read(&serde_json::json!({"version": 1, "workspaces": [], "sessions": [{"id": "s1", "title": "Solo Run"}]}))
                .unwrap();
        let mut d = Decision::default();
        run(&mut d, &state, "s1", "running", true);
        assert_eq!(run(&mut d, &state, "s1", "completed", true).as_deref(), Some("Solo Run"));
    }

    /// Verification.cs:1128 "completion notification does not fire for error or stopped".
    #[test]
    fn does_not_fire_for_error_or_stopped() {
        for status in ["error", "stopped"] {
            let (state, id) = snapshot("W", "S");
            let mut d = Decision::default();
            run(&mut d, &state, &id, "running", true);
            assert_eq!(run(&mut d, &state, &id, status, true), None, "{status}");
            assert_eq!(run(&mut d, &state, &id, "completed", true), None, "{status} forgot the run");
        }
    }

    /// Verification.cs:1129 "completion notification does not fire for completed without running".
    #[test]
    fn does_not_fire_for_completed_without_running() {
        let (state, id) = snapshot("W", "S");
        assert_eq!(run(&mut Decision::default(), &state, &id, "completed", true), None);
    }

    /// Verification.cs:1130 "completion notification does not fire twice for the same run".
    #[test]
    fn does_not_fire_twice_for_the_same_run() {
        let (state, id) = snapshot("W", "S");
        let mut d = Decision::default();
        run(&mut d, &state, &id, "running", true);
        run(&mut d, &state, &id, "completed", true);
        assert_eq!(run(&mut d, &state, &id, "completed", true), None);
    }

    /// Verification.cs:1131 "completion notification does not fire when preference is off".
    #[test]
    fn does_not_fire_when_preference_is_off() {
        let (state, id) = snapshot("W", "S");
        let mut d = Decision::default();
        run(&mut d, &state, &id, "running", false);
        assert_eq!(run(&mut d, &state, &id, "completed", false), None);
    }

    /// Verification.cs:1132 "completion notification strings match macOS".
    #[test]
    fn completion_notification_strings_match_macos() {
        let ko = Locale::new("ko");
        let expected = [
            ("NotificationTitle", "Mighty Code · 작업 완료"),
            ("NotificationBodyTemplate", "{title}의 작업이 완료되었습니다."),
            ("ToggleLabel", "작업 완료 시 Windows 알림"),
            ("StatusAllowed", "허용됨"),
            ("StatusDenied", "시스템 설정에서 알림을 허용하세요"),
            ("StatusNeedPermission", "권한 필요"),
            ("StatusVerificationMode", "검증 모드"),
            ("SettingsButton", "알림 설정"),
        ]
        .map(|(name, words)| (name, words.to_string()));
        let actual: Vec<(&str, String)> = STRINGS.iter().map(|(name, _)| (*name, text(&ko, name))).collect();
        crate::app_update_view::validate(&actual, &expected).unwrap();
        let with = |name: &str, value: Option<&str>| -> Vec<(&str, String)> {
            actual
                .iter()
                .filter(|(n, _)| value.is_some() || *n != name)
                .map(|(n, w)| (*n, if *n == name { value.unwrap_or_default().to_string() } else { w.clone() }))
                .collect()
        };
        let denied = text(&ko, "StatusDenied");
        assert!(crate::app_update_view::validate(&with("NotificationTitle", Some("")), &expected).is_err());
        assert!(
            crate::app_update_view::validate(&with("NotificationBodyTemplate", Some("{ title }의 작업이 완료되었습니다.")), &expected)
                .is_err()
        );
        assert!(crate::app_update_view::validate(&with("StatusAllowed", Some(&denied)), &expected).is_err());
        assert!(crate::app_update_view::validate(&with("SettingsButton", None), &expected).is_err());
    }

    /// Verification.cs:1404 `LocalizedStatusVerification` (the notification half): a language change reaches every word at once.
    #[test]
    fn language_changes_reach_notifications() {
        let en = Locale::new("en");
        assert_eq!(text(&en, "NotificationTitle"), "Mighty Code · Task complete");
        assert_eq!(body(&en, "Fixture"), "Fixture has finished.");
        for (name, _) in STRINGS {
            let words = text(&en, name);
            assert!(!words.trim().is_empty() && !words.starts_with("windows.") && !words.starts_with("usage.reset."), "{name}");
            assert!(!words.chars().any(|c| ('\u{AC00}'..='\u{D7A3}').contains(&c)), "{name} has no Hangul in English");
        }
        assert_eq!(text(&Locale::new("ko"), "NotificationTitle"), "Mighty Code · 작업 완료");
    }

    fn block<T>(future: impl std::future::Future<Output = T>) -> T {
        let waker = std::task::Waker::noop();
        let mut context = std::task::Context::from_waker(waker);
        let mut future = std::pin::pin!(future);
        loop {
            if let std::task::Poll::Ready(value) = future.as_mut().poll(&mut context) {
                return value;
            }
        }
    }

    /// Verification.cs:1133 "completion notification smoke sends one fixture call and records sent".
    #[test]
    fn smoke_sends_one_fixture_call_and_records_sent() {
        let (registered, sent) = (RefCell::new(0), RefCell::new(vec![]));
        let outcome = block(smoke_run(
            true,
            async || {
                *registered.borrow_mut() += 1;
                None
            },
            async |title: &str, id: &str| -> Result<(), String> {
                sent.borrow_mut().push((title.to_string(), id.to_string()));
                Ok(())
            },
        ))
        .unwrap();
        assert_eq!((*registered.borrow(), sent.borrow().clone()), (1, vec![(FIXTURE_TITLE.to_string(), FIXTURE_SESSION_ID.to_string())]));
        assert_eq!(outcome.to_json(), r#"{"status":"sent"}"#);
    }

    /// Verification.cs:1134 "completion notification smoke records skipped with a reason".
    #[test]
    fn smoke_records_skipped_with_a_reason() {
        let sends = RefCell::new(0);
        let send = async |_: &str, _: &str| -> Result<(), String> {
            *sends.borrow_mut() += 1;
            Ok(())
        };
        let unsupported = block(smoke_run(false, async || panic!("register is not asked when unsupported"), send)).unwrap();
        assert_eq!(unsupported, SmokeOutcome::Skipped(UNSUPPORTED_REASON.into()));
        assert_eq!(unsupported.to_json(), r#"{"status":"skipped","reason":"IsSupported false"}"#);
        let send = async |_: &str, _: &str| -> Result<(), String> {
            *sends.borrow_mut() += 1;
            Ok(())
        };
        let refused = block(smoke_run(true, async || Some("registration unavailable".to_string()), send)).unwrap();
        assert_eq!(refused, SmokeOutcome::Skipped("registration unavailable".into()));
        let send = async |_: &str, _: &str| -> Result<(), String> { Ok(()) };
        assert_eq!(block(smoke_run(true, async || Some("  ".to_string()), send)).unwrap(), SmokeOutcome::Skipped(UNKNOWN_REASON.into()));
        assert_eq!(*sends.borrow(), 0);
    }

    /// Verification.cs:1135 "completion notification smoke failure after support is a failure".
    #[test]
    fn smoke_failure_after_support_is_a_failure() {
        let failed = block(smoke_run(true, async || None, async |_: &str, _: &str| Err("toast call timed out".to_string())));
        assert_eq!(failed, Err("toast call timed out".to_string()));
    }

    /// Verification.cs:1136 "completion notification smoke keeps the saved state version at 1": a default state's setting is on and its
    /// version 1; the smoke reads and writes no state (it has none to touch).
    #[test]
    fn smoke_keeps_the_saved_state_version() {
        let state = AppSnapshot::default();
        assert!(state.completion_notifications_enabled);
        assert!(state.to_json().starts_with(r#"{"version":1,"#), "the saved state's version is 1");
    }
}
