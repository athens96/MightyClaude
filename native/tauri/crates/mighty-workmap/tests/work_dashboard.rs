//! C# WorkDashboardVerification.cs (Verification.cs:374-380): the counts and ordering of the macOS / phone work dashboard (mighty-workmap's
//! `dashboard` with mighty-core's `shell` card, badges and order), and the window preferences it keeps (the settings category, the
//! sidebar's fold and width, the background strip's open state) through the state store.

mod common;

use std::sync::Arc;

use common::ko;
use mighty_core::model::{AgentRunTiming, AppSnapshot, BackgroundTask, BackgroundWork, LogEntry, RunSession, SessionUsage};
use mighty_core::permission::ToolPermissionRequest;
use mighty_core::shell::{self, Attention, Counts};
use mighty_core::time::DateTimeOffset;
use mighty_workmap::dashboard::{self, Age, Stats};

fn pane(id: &str, status: &str, kind: &str) -> RunSession {
    RunSession {
        id: Some(id.into()),
        workspace_id: Some("w".into()),
        kind: Some(kind.into()),
        status: Some(status.into()),
        created_at: Some("2026-10-01T09:00:00Z".into()),
        ..RunSession::default()
    }
}

fn request(id: &str, question: bool, state: &str) -> ToolPermissionRequest {
    let mut value = ToolPermissionRequest::new(id, "r", id, if question { "AskUserQuestion" } else { "Bash" }, "{}", "fixture");
    value.state = state.into();
    value.can_answer_questions = question;
    value
}

fn at(text: &str) -> DateTimeOffset {
    mighty_core::time::parse(Some(text)).unwrap()
}

fn attention(questions: usize) -> Attention {
    Attention { questions, permissions: 0 }
}

/// WorkDashboardVerification.cs:11 `CountsAndAttentionMatchMac`: a waiting pane also counts as running, whereas the waiting number counts
/// individual requests, not panes.
#[test]
fn work_dashboard_counts_and_attention_match_mac() {
    let sessions = [
        pane("run", "running", "claude"),
        pane("asking", "running", "claude"),
        pane("done", "completed", "claude"),
        pane("err", "error", "claude"),
        pane("shell", "running", "shell"),
        pane("io", "running", "agent-terminal"),
        pane("files", "completed", "files"),
    ];
    let pending = |id: &str| match id {
        "asking" => dashboard::pending(&[request("q1", true, "pending"), request("q2", true, "pending"), request("p1", false, "pending")]),
        "done" => dashboard::pending(&[request("old", false, "allowed")]),
        "io" => dashboard::pending(&[request("ignored", false, "pending")]),
        _ => Attention::default(),
    };
    assert_eq!(
        dashboard::count(sessions.iter(), &pending),
        Stats { running: 3, waiting: 3, done: 1 },
        "Mac dashboard fixture counts changed"
    );
    assert_eq!(dashboard::count([].iter(), &pending), Stats::default(), "empty dashboard is zero");
    assert_eq!(
        shell::badges(sessions.iter(), &pending),
        Counts { questions: 2, permissions: 1, errors: 1, running: 2, done: 1, stopped: 0, idle: 0 },
        "waiting panes must not also inflate running workspace badges"
    );
    assert_eq!(
        shell::badges([pane("a", "failed", "claude"), pane("b", "cancelled", "claude")].iter(), &|_| Attention::default()),
        Counts { errors: 1, stopped: 1, ..Counts::default() },
        "status aliases share Mac design tones"
    );
    assert_eq!(
        shell::make_card(&pane("asking", "error", "claude"), attention(1)).display_status,
        "waiting",
        "a question is actionable even if the run reported error"
    );
    assert_eq!(
        dashboard::pending_with_card(&[], true),
        Attention { questions: 0, permissions: 1 },
        "an Antigravity card is one more permission request"
    );
}

/// WorkDashboardVerification.cs:29 `CardsRespectUsageIdentityAndStablePriority`.
#[test]
fn work_dashboard_cards_respect_usage_identity_and_stable_priority() {
    // `new SessionUsage { Provider = "codex", ContextUsedTokens = 41, ContextWindowTokens = window }`.
    let usage = |window: Option<i64>| SessionUsage {
        provider: "codex".into(),
        source: String::new(),
        token_scope: String::new(),
        model: None,
        selected_model: None,
        provider_session_id: None,
        input_tokens: None,
        output_tokens: None,
        cache_read_tokens: None,
        cache_write_tokens: None,
        reasoning_tokens: None,
        total_tokens: None,
        context_used_tokens: Some(41),
        context_window_tokens: window,
        cost_usd: None,
        cost_scope: None,
        rate_limits: None,
        rate_limits_updated_at: None,
        updated_at: String::new(),
    };
    let own = RunSession { provider: Some("codex".into()), session_usage: Some(usage(Some(100))), ..pane("own", "idle", "claude") };
    assert_eq!(shell::make_card(&own, Attention::default()).context_percent, Some(41.0), "direct own-provider context preserved");
    let switched = RunSession { provider: Some("claude".into()), ..own.clone() };
    assert_eq!(shell::make_card(&switched, Attention::default()).context_percent, None, "provider switch cannot relabel old usage");
    let unknown = RunSession { session_usage: Some(usage(None)), ..own.clone() };
    assert_eq!(shell::make_card(&unknown, Attention::default()).context_percent, None, "missing window is unknown, not zero");
    let start = at("2026-10-01T10:00:00Z");
    let invalid = RunSession {
        run_timing: Some(AgentRunTiming {
            started_at: start,
            last_observed_at: DateTimeOffset::from_utc_ticks(start.utc_ticks() - 10_000_000),
            finished_at: None,
            is_approximate: false,
        }),
        ..own.clone()
    };
    let card = shell::make_card(&invalid, Attention::default());
    assert!(card.session.run_timing.as_ref().is_none_or(|t| !t.is_valid()), "invalid timing is absent");
    let default_model = RunSession { model: Some(" default ".into()), ..own.clone() };
    assert_eq!(shell::make_card(&default_model, Attention::default()).model(), None, "default is not a concrete model");
    let panes = [
        pane("idle", "idle", "claude"),
        pane("stop", "interrupted", "claude"),
        pane("old", "completed", "claude"),
        RunSession { created_at: Some("2026-10-01T10:00:00Z".into()), ..pane("new", "completed", "claude") },
        pane("err", "failed", "claude"),
        pane("run", "running", "claude"),
        pane("wait", "running", "claude"),
    ];
    let cards: Vec<_> =
        panes.iter().map(|s| shell::make_card(s, if s.id() == "wait" { attention(1) } else { Attention::default() })).collect();
    let ordered: Vec<String> = shell::ordered(cards).iter().map(|c| c.session.id().to_string()).collect();
    assert_eq!(ordered, ["wait", "run", "err", "new", "old", "stop", "idle"], "priority matches Mac and phone");
    let same = [pane("a", "idle", "claude"), pane("b", "idle", "claude"), pane("c", "idle", "claude")];
    let kept: Vec<String> = shell::ordered(same.iter().map(|s| shell::make_card(s, Attention::default())).collect())
        .iter()
        .map(|c| c.session.id().to_string())
        .collect();
    assert_eq!(kept, ["a", "b", "c"], "equal rank/time retains source order");
}

/// WorkDashboardVerification.cs:44 `ActivityAndAgeRemainBoundedAndTruthful`.
#[test]
fn work_dashboard_activity_and_age_remain_bounded_and_truthful() {
    let locale = ko();
    let now = at("2026-10-01T10:20:00Z");
    let failing = RunSession {
        logs: Some(vec![LogEntry::new("entry".into(), "error", "\n  first line\nsecond line".into(), "2026-10-01T10:17:00Z".into(), None)]),
        ..pane("tool", "error", "claude")
    };
    let card = shell::make_card(&failing, Attention::default());
    assert!(
        card.last_activity.as_deref() == Some("first line")
            && card.activity_is_error
            && card.updated_at.map(|u| u.utc_ticks()) == Some(now.utc_ticks() - 3 * 600_000_000),
        "newest meaningful line and source time retained"
    );
    assert!(shell::sidebar_detail(&locale, &card, now).contains("first line"), "failed run exposes actual reason");
    let completed = RunSession { status: Some("completed".into()), ..failing.clone() };
    assert!(
        !shell::sidebar_detail(&locale, &shell::make_card(&completed, Attention::default()), now).contains("first line"),
        "old error cannot label successful run failed"
    );
    let unicode = "👩\u{200d}💻".repeat(250);
    let emoji = RunSession {
        logs: Some(vec![LogEntry::new("entry".into(), "assistant", unicode, "2026-10-01T10:17:00Z".into(), None)]),
        ..failing.clone()
    };
    let card = shell::make_card(&emoji, Attention::default());
    let text = card.last_activity.clone().unwrap_or_default();
    assert!(mighty_core::wire::text_elements(&text, 200).0 == text && !text.is_empty(), "Unicode activity is capped without broken UTF-16");
    assert_eq!(
        dashboard::since(DateTimeOffset::from_utc_ticks(now.utc_ticks() + 6_000_000_000), now),
        Age { unit: "now", count: 0 },
        "future activity never negative"
    );
    assert_eq!(
        dashboard::since(DateTimeOffset::from_utc_ticks(now.utc_ticks() - 26 * 36_000_000_000), now),
        Age { unit: "days", count: 1 },
        "largest whole unit"
    );
    assert_eq!(
        shell::sidebar_detail(&locale, &shell::make_card(&pane("shell", "idle", "shell"), Attention::default()), now),
        "",
        "non-agent does not inherit provider metadata"
    );
}

/// WorkDashboardVerification.cs:61 `SettingsSelectionSurvivesPersistence` (its normalize part; the settings categories are W6's
/// `SettingsNavigation`).
#[test]
fn work_dashboard_settings_selection_survives_persistence() {
    let saved = mighty_core::normalize::normalize(
        &AppSnapshot { settings_pane: Some("styles".into()), sidebar_width: 999.0, ..AppSnapshot::default() },
        false,
    )
    .unwrap();
    assert!(
        saved.settings_pane.as_deref() == Some("styles") && saved.sidebar_width == mighty_core::normalize::SIDEBAR_MAX,
        "valid category survives and the sidebar is bounded"
    );
    let reset =
        mighty_core::normalize::normalize(&AppSnapshot { settings_pane: Some("unknown".into()), sidebar_width: f64::NAN, ..saved }, false)
            .unwrap();
    assert!(
        reset.settings_pane.as_deref() == Some("general") && reset.sidebar_width == mighty_core::normalize::SIDEBAR_DEFAULT,
        "old/corrupt selection safely defaults"
    );
}

fn store(directory: &std::path::Path) -> mighty_core::store::Store {
    let mut store = mighty_core::store::Store::new(directory.to_path_buf(), None, true);
    store.load(&ko()).unwrap();
    store
}

/// WorkDashboardVerification.cs:72 `SidebarCollapsedPersistsAndKeepsWidth`.
#[test]
fn work_dashboard_sidebar_collapsed_persists_and_keeps_width() {
    use mighty_core::Wire;
    let legacy = AppSnapshot::read(
        &serde_json::json!({"version": 1, "workspaces": [], "sessions": [], "layout": "grid", "theme": "dark", "sidebarWidth": 300}),
    )
    .unwrap();
    assert!(!legacy.sidebar_collapsed && !AppSnapshot::default().sidebar_collapsed, "old state without the field opens the sidebar");
    let normalized =
        mighty_core::normalize::normalize(&AppSnapshot { sidebar_collapsed: true, sidebar_width: 300.0, ..AppSnapshot::default() }, false)
            .unwrap();
    assert!(normalized.sidebar_collapsed && normalized.sidebar_width == 300.0, "normalize keeps the fold and the width");
    let directory = common::temp("sidebar");
    for collapsed in [true, false] {
        store(&directory).save(&AppSnapshot { sidebar_collapsed: collapsed, sidebar_width: 310.0, ..AppSnapshot::default() }).unwrap();
        let text = std::fs::read_to_string(directory.join("workspace-state.json")).unwrap();
        assert!(!collapsed || text.contains("\"sidebarCollapsed\":true"), "the saved file names sidebarCollapsed as true; got {text}");
        let mut again = mighty_core::store::Store::new(directory.clone(), None, true);
        let restored = again.load(&ko()).unwrap();
        assert!(
            restored.sidebar_collapsed == collapsed && restored.sidebar_width == 310.0,
            "reload restores collapsed={collapsed} width=310"
        );
    }
    common::remove(&directory);
}

/// WorkDashboardVerification.cs:97 `BackgroundWorkOpenPersistsAndTheOldHideSwitchIsDropped`.
#[test]
fn work_dashboard_background_work_open_persists_and_the_old_hide_switch_is_dropped() {
    use mighty_core::Wire;
    let legacy = AppSnapshot::read(&serde_json::json!({"version": 1, "workspaces": [], "sessions": [], "layout": "grid", "theme": "dark",
        "sidebarWidth": 300, "showsBackgroundWork": false, "backgroundWorkOpen": true}))
    .unwrap();
    assert!(
        legacy.background_work_open == Some(true) && AppSnapshot::default().background_work_open.is_none(),
        "old state with the hide switch still loads and keeps the strip open"
    );
    let invalid = AppSnapshot::read(&serde_json::json!({"version": 1, "showsBackgroundWork": 0, "backgroundWorkOpen": "true"})).unwrap();
    assert!(invalid.background_work_open.is_none(), "only a JSON boolean counts");
    let normalized =
        mighty_core::normalize::normalize(&AppSnapshot { background_work_open: Some(true), ..AppSnapshot::default() }, false).unwrap();
    assert_eq!(normalized.background_work_open, Some(true), "normalize keeps the open state");
    let directory = common::temp("background");
    for open in [true, false] {
        store(&directory).save(&AppSnapshot { background_work_open: Some(open), ..AppSnapshot::default() }).unwrap();
        let mut again = mighty_core::store::Store::new(directory.clone(), None, true);
        let restored = again.load(&ko()).unwrap();
        let raw = std::fs::read_to_string(directory.join("workspace-state.json")).unwrap();
        assert!(
            restored.background_work_open == Some(open) && !raw.contains("showsBackgroundWork"),
            "reload restores open={open} and the file carries no hide switch"
        );
    }
    common::remove(&directory);
    let work = BackgroundWork {
        tasks: Some(vec![BackgroundTask {
            id: Some("a".into()),
            kind: Some("agent".into()),
            description: Some("look".into()),
            started_at: Some("2026-10-06T01:00:00.000Z".into()),
            status: Some("running".into()),
            tool_use_id: None,
            summary: None,
            ended_at: None,
        }]),
        turn_ended: true,
    };
    for mighty in [false, true] {
        assert!(
            mighty_composer::presentation::plan::shows_background_strip(Some(&work), mighty, false),
            "the strip draws in either view (mighty={mighty})"
        );
    }
    let _ = Arc::new(());
}
