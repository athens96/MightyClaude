//! C# `PendingVerification` (Verification.cs:1233-1237), held to `native/contracts/fixtures/pending.json`.

mod common;

use std::collections::HashSet;

use mighty_composer::pending::{
    self, PENDING_MIN_VISIBLE, PENDING_SHOW_AFTER, PendingIndicator, PendingState, SubmitOutcome, SubmitReceipt, SubmitResponse, scroll,
};
use mighty_core::time::DateTimeOffset;
use serde_json::{Value, json};

fn number(entry: &Value, name: &str) -> Option<f64> {
    entry.get(name).and_then(Value::as_f64)
}

/// Verification.cs:1233 "pending: every display case of the shared rule holds, in milliseconds and as a pending state"
/// (EveryDisplayCaseHolds).
#[test]
fn every_display_case_holds() {
    let fixture = common::fixture("pending.json");
    let cases = fixture["display"].as_array().unwrap();
    assert!(!cases.is_empty());
    // The clock's start, and a date far from it (2026-10-27T12:34:56Z + 1234567 ticks).
    let far = mighty_core::time::parse(Some("2026-10-27T12:34:56.1234567+00:00")).unwrap();
    let epoch = mighty_core::time::parse(Some("1970-01-01T00:00:00.0000000+00:00")).unwrap();
    for entry in cases {
        let name = entry["name"].as_str().unwrap();
        let (started, ended, now) = (number(entry, "startedAt").unwrap(), number(entry, "endedAt"), number(entry, "now").unwrap());
        let failed = entry.get("failed") == Some(&Value::Bool(true));
        let expected = entry["expectVisible"].as_bool().unwrap();
        assert_eq!(pending::visible(started, ended, now, failed), expected, "{name}: in milliseconds");
        for origin in [epoch, far] {
            let at = |ms: f64| DateTimeOffset::from_utc_ticks(origin.utc_ticks() + (ms * 10_000.0).round_ties_even() as i64);
            let state = if failed {
                PendingState::Failed { message: "broke".into(), since: at(started), at: ended.map(at) }
            } else if let Some(end) = ended {
                PendingState::Done { since: at(started), at: at(end) }
            } else {
                PendingState::Pending { since: at(started) }
            };
            assert_eq!(pending::state_visible(&state, at(now)), expected, "{name}: as {state:?}");
            let shown = if !expected {
                PendingIndicator::Hidden
            } else if failed && now >= ended.unwrap() {
                PendingIndicator::Failed("broke".into())
            } else {
                PendingIndicator::Loading
            };
            assert_eq!(pending::indicator(&state, at(now)), shown, "{name}");
        }
    }
}

/// Verification.cs:1234 "pending: idle hides, and a failure without an end shows its message now" (IdleAndAnOpenFailure).
#[test]
fn idle_and_an_open_failure() {
    let now = DateTimeOffset::now();
    assert_eq!(pending::indicator(&PendingState::Idle, now), PendingIndicator::Hidden);
    assert!(!pending::state_visible(&PendingState::Idle, now));
    assert_eq!(
        pending::indicator(&PendingState::Failed { message: "gone".into(), since: now, at: None }, now),
        PendingIndicator::Failed("gone".into())
    );
    let earlier = DateTimeOffset::from_utc_ticks(now.utc_ticks() - 5 * 10_000_000);
    assert_eq!(
        pending::indicator(&PendingState::Failed { message: "gone".into(), since: earlier, at: None }, now),
        PendingIndicator::Failed("gone".into())
    );
    assert!(pending::visible(0.0, None, 0.0, true));
    assert!(
        pending::visible(0.0, None, PENDING_SHOW_AFTER as f64, false)
            && !pending::visible(0.0, None, (PENDING_SHOW_AFTER - 1) as f64, false)
    );
    assert!(pending::visible(0.0, Some(200.0), (150 + PENDING_MIN_VISIBLE) as f64, false));
    assert!(!pending::visible(0.0, Some(200.0), (151 + PENDING_MIN_VISIBLE) as f64, false));
}

fn outcome(value: &Value) -> Option<SubmitOutcome> {
    match value.as_str() {
        Some("started") => Some(SubmitOutcome::Started),
        Some("queued") => Some(SubmitOutcome::Queued),
        Some("steered") => Some(SubmitOutcome::Steered),
        _ => None,
    }
}

/// Verification.cs:1235 "pending: every shared submit response parses as the fixture says" (EverySubmitResponseParses).
#[test]
fn every_submit_response_parses() {
    let fixture = common::fixture("pending.json");
    for entry in fixture["submitResponses"].as_array().unwrap() {
        let (raw, parsed) = (&entry["raw"], &entry["parsed"]);
        let expected = SubmitResponse {
            accepted: parsed["accepted"].as_bool().unwrap(),
            entry_id: parsed["entryId"].as_str().map(str::to_string),
            run_id: parsed["runId"].as_str().map(str::to_string),
            outcome: outcome(&parsed["outcome"]),
        };
        assert_eq!(SubmitResponse::parse(Some(raw)), expected, "{}", entry["name"]);
        assert_eq!(SubmitResponse::parse_text(&raw.to_string()), expected, "{} as text", entry["name"]);
    }
    assert_eq!(SubmitResponse::parse(None), SubmitResponse::not_accepted());
    assert_eq!(SubmitResponse::parse_text("not json"), SubmitResponse::not_accepted());
    assert_eq!(SubmitResponse::parse_text(""), SubmitResponse::not_accepted());
    assert_eq!(SubmitResponse::parse_text(r#"{"accepted":1}"#), SubmitResponse::not_accepted());
    assert_eq!(SubmitResponse::parse_text(r#"{"accepted":"dropped"}"#), SubmitResponse::not_accepted());
    assert_eq!(SubmitResponse::parse_text(r#"["started"]"#), SubmitResponse::not_accepted());
    for o in SubmitOutcome::ALL {
        assert_eq!(SubmitResponse::parse_text(&format!(r#"{{"outcome":"{}"}}"#, o.word())).outcome, Some(o));
    }
}

/// Verification.cs:1236 "pending: the host's phone reply and the page's send answer parse back with their ids" (TheHostAnswersParseBack).
#[test]
fn the_host_answers_parse_back() {
    let read = |body: Value| SubmitResponse::parse(Some(&body));
    let started = SubmitReceipt::with_ids("started", Some("entry-1"), Some("run-1"));
    let phone = serde_json::to_value(started.mobile_reply()).unwrap();
    assert!(phone["protocol"] == 1 && phone["accepted"] == "started" && phone["outcome"] == "started", "{phone}");
    let full = SubmitResponse {
        accepted: true,
        entry_id: Some("entry-1".into()),
        run_id: Some("run-1".into()),
        outcome: Some(SubmitOutcome::Started),
    };
    assert_eq!(read(phone), full);
    let queued = serde_json::to_string(&SubmitReceipt::new("queued").mobile_reply()).unwrap();
    assert_eq!(queued, r#"{"protocol":1,"accepted":"queued","outcome":"queued"}"#);
    assert_eq!(
        read(serde_json::from_str(&queued).unwrap()),
        SubmitResponse { accepted: true, outcome: Some(SubmitOutcome::Queued), ..Default::default() }
    );
    let answer = serde_json::to_value(started.send_answer(Some("client-7"), "", Some(json!({"busy": true})))).unwrap();
    assert!(
        answer["accepted"] == true && answer["clientId"] == "client-7" && answer["draft"] == "" && answer["state"]["busy"] == true,
        "{answer}"
    );
    assert_eq!(read(answer), full);
    let blocked = serde_json::to_value(SubmitReceipt::new("blocked").send_answer::<Value>(Some("client-8"), "hello", None)).unwrap();
    assert!(blocked["outcome"] == "blocked" && blocked.get("entryId").is_none());
    assert_eq!(read(blocked), SubmitResponse::not_accepted());
    for word in ["worktree", "answered", "builtin"] {
        let body = serde_json::to_value(SubmitReceipt::new(word).send_answer::<Value>(None, "", None)).unwrap();
        assert_eq!(read(body), SubmitResponse { accepted: true, ..Default::default() }, "{word}");
    }
    for word in ["notStarted", "blocked", "none"] {
        assert!(!SubmitReceipt::new(word).accepted(), "{word}");
    }
    assert!(SubmitReceipt::new("started").send_answer::<Value>(Some("../bad id"), "", None).client_id.is_none());
    assert!(SubmitReceipt::new("started").send_answer::<Value>(None, "", None).client_id.is_none());
}

/// Verification.cs:1237 "pending: every shared scroll target holds, and the view follows the pane's mode" (EveryScrollTargetHolds).
#[test]
fn every_scroll_target_holds() {
    let fixture = common::fixture("pending.json");
    let mut views = HashSet::new();
    for entry in fixture["scrollTargets"].as_array().unwrap() {
        let (input, expect) = (&entry["input"], &entry["expect"]);
        let view = match input["view"].as_str().unwrap().to_ascii_lowercase().as_str() {
            "basic" => scroll::View::Basic,
            "diagram" => scroll::View::Diagram,
            "timeline" => scroll::View::Timeline,
            "pipeline" => scroll::View::Pipeline,
            other => panic!("view {other}"),
        };
        views.insert(view);
        let decision =
            scroll::decide(view, input["own"].as_bool().unwrap(), input["chosenBoard"] == "newest", input["nearBottom"].as_bool().unwrap());
        let target = match expect["scroll"].as_str().unwrap().to_ascii_lowercase().as_str() {
            "torequest" => scroll::Target::ToRequest,
            "follow" => scroll::Target::Follow,
            "none" => scroll::Target::None,
            other => panic!("target {other}"),
        };
        assert_eq!(
            decision,
            scroll::Decision { scroll: target, reset_board: expect["resetBoard"].as_bool().unwrap() },
            "{}",
            entry["name"]
        );
    }
    assert_eq!(views.len(), 4, "the cases cover all four views");
    for mode in [None, Some("diagram"), Some("timeline"), Some("structure"), Some("unknown")] {
        assert_eq!(scroll::view_for(false, mode), scroll::View::Basic);
    }
    assert_eq!(scroll::view_for(true, None), scroll::View::Diagram);
    assert_eq!(scroll::view_for(true, Some("unknown")), scroll::View::Diagram);
    assert_eq!(scroll::view_for(true, Some("diagram")), scroll::View::Diagram);
    assert_eq!(scroll::view_for(true, Some("timeline")), scroll::View::Timeline);
    assert_eq!(scroll::view_for(true, Some("structure")), scroll::View::Pipeline);
}
