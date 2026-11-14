//! C# LocalizedStatusVerification `LanguageChangesReachNotificationsAndUsage` (Verification.cs:1404), its usage half (the
//! notification half is W6-A's), and the English label of AntigravityAccountUsageVerification `TheUsageReplyGivesFourGroupedWindows`.
//! The app's language is the process's own, so these checks live in a test program of their own, in one test.

mod common;

use std::sync::Arc;

use chrono::{DateTime, Local, TimeDelta, Utc};
use mighty_accounts::usage::{self, AccountUsageService, AccountUsageSnapshot, AccountUsageStatus, AccountUsageWindow, UsageProbe, copy};
use mighty_core::json::Wire;
use mighty_core::model::AppSnapshot;

fn stamp() -> DateTime<Utc> {
    DateTime::parse_from_rfc3339("2026-09-20T10:00:00Z").unwrap().with_timezone(&Utc)
}

/// LocalizedStatusVerification.cs:5 `LanguageChangesReachNotificationsAndUsage`, the usage half: the saved language reaches every
/// visible string and the real projections, dates in that language's culture whatever the host's, and switching back keeps no
/// English.
#[tokio::test]
async fn language_changes_reach_usage() {
    mighty_run::strings::set_language("ko");
    let korean_chip = copy::chip_before_first_run();
    mighty_run::strings::set_language("en");
    assert!(
        copy::chip_before_first_run() == "Shown after a run" && usage::window_label("30m") == "30 min" && usage::window_label("2h") == "2h",
        "usage status and arbitrary duration windows must follow English"
    );
    for (field, value) in copy::all() {
        assert!(
            !value.trim().is_empty() && !value.starts_with("windows.") && !value.starts_with("usage.reset."),
            "all visible status strings must resolve: {field}"
        );
        assert!(
            !value.chars().any(|c| ('\u{AC00}'..='\u{D7A3}').contains(&c)),
            "English status copy must not retain Korean literals: {field}"
        );
    }
    let fixture = AccountUsageSnapshot {
        status: "available".into(),
        fetched_at: Some(usage::timestamp(stamp())),
        windows: vec![AccountUsageWindow {
            resets_at: Some(usage::timestamp(stamp() + TimeDelta::hours(2))),
            ..AccountUsageWindow::new("five_hour", 25.0)
        }],
        ..AccountUsageSnapshot::new("codex")
    };
    let probe: UsageProbe = Arc::new(move |_| {
        let value = fixture.clone();
        Box::pin(async move { Ok(value) })
    });
    let clock: usage::Clock = Arc::new(stamp);
    let status = AccountUsageStatus::new(AccountUsageService::new(probe, clock.clone()), clock);
    let state = serde_json::json!({"workspaces": [{"id": "w"}], "sessions": [{"id": "c", "workspaceId": "w", "kind": "claude", "provider": "codex"}]});
    status.update(&AppSnapshot::read(&state).unwrap());
    status.refresh(false).await;
    let cards = status.cards();
    let chips = status.chips();
    assert!(
        chips.len() == 1 && chips[0].text == "Session 25%" && cards[0].windows.len() == 1 && cards[0].windows[0].used == "25% used",
        "real status projections must use localized labels and templates: {chips:?} {cards:?}"
    );
    assert!(cards[0].checked_at.as_deref().is_some_and(|c| c.starts_with("Checked ")), "{:?}", cards[0].checked_at);
    let local = (stamp() + TimeDelta::hours(2)).with_timezone(&Local);
    let expected = format!("Resets {}", local.format("%-m/%-d/%Y %-I:%M %p"));
    assert_eq!(
        cards[0].windows[0].reset.as_deref(),
        Some(expected.as_str()),
        "explicit English controls date formatting independently of host culture"
    );
    // AntigravityAccountUsageVerification.cs:38: the grouped label in English.
    assert_eq!(usage::window_label("weekly@claude_gpt"), "Claude & GPT · Weekly");
    mighty_run::strings::set_language("ko");
    assert_eq!(copy::chip_before_first_run(), korean_chip, "switching back must not retain cached English copy");
    assert_eq!(usage::window_label("weekly@claude_gpt"), "Claude·GPT · 주간");
    status.close().await;
}
