//! C# `NextActionsVerification` (Verification.cs:1041-1042), held to `native/contracts/fixtures/next-actions.json`.

mod common;

use mighty_composer::next_actions::{self, NextAction};
use mighty_core::model::LogEntry;

/// Verification.cs:1041 "next actions match every shared Mac and phone vector" (NextActionsVerification.SharedContract).
#[test]
fn shared_contract() {
    let rows = common::fixture("next-actions.json");
    let rows = rows.as_array().unwrap();
    assert!(rows.len() >= 20, "shared fixture was not loaded");
    for row in rows {
        let line = row["line"].as_str().unwrap();
        let expected: Vec<NextAction> = row["options"]
            .as_array()
            .unwrap()
            .iter()
            .map(|o| NextAction::new(o["label"].as_str().unwrap(), o["fill"].as_str().unwrap()))
            .collect();
        assert_eq!(next_actions::parse(line), expected, "breadcrumb differs from Mac/phone: {line}");
    }
}

fn entry(id: &str, kind: &str, text: &str) -> LogEntry {
    LogEntry::new(id.into(), kind, text.into(), String::new(), None)
}

/// Verification.cs:1042 "next actions invalidate old replies and preserve drafts" (NextActionsVerification.LatestReplyAndDraftSafety).
#[test]
fn latest_reply_and_draft_safety() {
    let reply = entry("a", "assistant", "◆ done → next: `ooo run` or Review");
    let latest = next_actions::latest(&[reply.clone(), entry("s", "system", "Read")]).expect("system log should retain last suggestions");
    assert!(latest.0 == "a" && latest.1.len() == 2);
    assert!(next_actions::latest(&[reply.clone(), entry("u", "user", "next")]).is_none());
    assert!(next_actions::latest(&[reply, entry("a2", "assistant", "plain")]).is_none());
    assert!(next_actions::latest(&[]).is_none());
    assert_eq!(NextAction::new("After `ooo run`", "ooo run").display_label(), "After ooo run");
    assert_eq!(NextAction::new("``", "``").display_label(), "``");
    for draft in ["", " \n\u{3000}"] {
        assert_eq!(next_actions::insertion(draft, "ooo run"), (true, "ooo run".into()));
    }
    for draft in ["draft", "draft "] {
        assert_eq!(next_actions::insertion(draft, "ooo run"), (false, "\nooo run".into()));
    }
    for draft in ["draft\n", "draft\r\n", "draft\n\n"] {
        assert_eq!(next_actions::insertion(draft, "ooo run"), (false, "ooo run".into()));
    }
    assert_eq!(next_actions::insertion("\u{a0}", "x"), (false, "\nx".into()), "NBSP is user content");
}
