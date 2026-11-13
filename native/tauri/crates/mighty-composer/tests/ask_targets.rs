//! C# `AskTargetsVerification` (Verification.cs:1264-1269), held to `native/contracts/fixtures/ask-targets.json`.

mod common;

use std::collections::HashSet;

use mighty_composer::ask_targets::{self, ALL, AskTarget, SKILL};
use mighty_core::model::{LogEntry, RunSession};
use serde_json::Value;

fn set(entry: &Value, key: &str) -> HashSet<String> {
    entry[key].as_array().unwrap().iter().map(|v| v.as_str().unwrap().to_string()).collect()
}

fn target(id: Option<&str>) -> Option<AskTarget> {
    id.map(|id| *ALL.iter().find(|t| t.id == id).unwrap_or_else(|| panic!("no target {id}")))
}

/// Verification.cs:1264 "ask targets: the targets and the skill are the fixture's" (TheTargetsAndSkillAreTheFixtures).
#[test]
fn the_targets_and_skill_are_the_fixtures() {
    let fixture = common::fixture("ask-targets.json");
    assert_eq!(fixture["skill"], SKILL);
    let targets = fixture["targets"].as_array().unwrap();
    assert_eq!(targets.iter().map(|t| t["id"].as_str().unwrap()).collect::<Vec<_>>(), ALL.iter().map(|t| t.id).collect::<Vec<_>>());
    assert_eq!(targets.iter().map(|t| t["label"].as_str().unwrap()).collect::<Vec<_>>(), ALL.iter().map(|t| t.label).collect::<Vec<_>>());
    assert_eq!(
        targets.iter().map(|t| t["executable"].as_str().unwrap()).collect::<Vec<_>>(),
        ALL.iter().map(|t| t.executable).collect::<Vec<_>>()
    );
    assert!(!ALL.iter().any(|t| t.id == "claude" || t.id == "muse"));
}

/// Verification.cs:1265 "ask targets: every shared available, effective and wrap case holds, and a wrapped send is never wrapped
/// again" (EveryAvailableEffectiveAndWrapCaseHolds).
#[test]
fn every_available_effective_and_wrap_case_holds() {
    let fixture = common::fixture("ask-targets.json");
    for entry in fixture["available"].as_array().unwrap() {
        let expected: Vec<&str> = entry["expect"].as_array().unwrap().iter().map(|v| v.as_str().unwrap()).collect();
        let result = ask_targets::available(entry["isClaudeAgent"].as_bool().unwrap(), &set(entry, "slash"), &set(entry, "installed"));
        assert_eq!(result.iter().map(|t| t.id).collect::<Vec<_>>(), expected, "available {}", entry["name"]);
    }
    for entry in fixture["effective"].as_array().unwrap() {
        let ids = set(entry, "available");
        let offered: Vec<AskTarget> = ALL.iter().copied().filter(|t| ids.contains(t.id)).collect();
        let result = ask_targets::effective(entry["saved"].as_str(), &offered);
        assert_eq!(result.map(|t| t.id), entry["expect"].as_str(), "effective {}", entry["name"]);
    }
    for entry in fixture["wrap"].as_array().unwrap() {
        let result = ask_targets::wrap(entry["text"].as_str().unwrap(), target(entry["target"].as_str()));
        assert_eq!(result.as_deref(), entry["expect"].as_str(), "wrap {}", entry["name"]);
    }
    let wrapped = ask_targets::wrap("review this", target(Some("codex"))).unwrap();
    assert_eq!(ask_targets::wrap(&wrapped, target(Some("codex"))).as_deref(), Some(wrapped.as_str()));
    assert_eq!(ask_targets::wrap(&wrapped, target(Some("grok"))).as_deref(), Some(wrapped.as_str()));
}

fn s(items: &[&str]) -> HashSet<String> {
    items.iter().map(|i| i.to_string()).collect()
}

/// Verification.cs:1266 "ask targets: a choice falls back only once known to be gone, and an unreliable PATH read keeps what was
/// found" (AChoiceFallsBackOnlyWhenKnownToBeGone).
#[test]
fn a_choice_falls_back_only_when_known_to_be_gone() {
    let codex: Vec<AskTarget> = ALL.iter().copied().filter(|t| t.id == "codex").collect();
    assert!(ask_targets::fell_back(Some("gemini"), &codex, true, true));
    assert!(ask_targets::fell_back(Some("codex"), &[], true, true));
    assert!(!ask_targets::fell_back(Some("codex"), &codex, true, true));
    assert!(!ask_targets::fell_back(None, &[], true, true));
    assert!(!ask_targets::fell_back(Some("gemini"), &codex, false, true));
    assert!(!ask_targets::fell_back(Some("gemini"), &codex, true, false));
    let all = ask_targets::probed(&s(&["codex"]), &s(&["codex", "agy"]), true);
    assert!(all.0 == s(&["codex", "agy"]) && all.1);
    let fewer = ask_targets::probed(&s(&["codex", "agy"]), &s(&["agy"]), true);
    assert!(fewer.0 == s(&["agy"]) && fewer.1);
    let first = ask_targets::probed(&s(&[]), &s(&[]), true);
    assert!(first.0.is_empty() && first.1);
    let hiccup = ask_targets::probed(&s(&["codex"]), &s(&[]), true);
    assert!(hiccup.0 == s(&["codex"]) && !hiccup.1);
    let fallback = ask_targets::probed(&s(&["codex"]), &s(&["agy"]), false);
    assert!(fallback.0 == s(&["codex", "agy"]) && !fallback.1);
}

/// Verification.cs:1267 "ask targets: a wrapped send titles its pane by the question" (TheQuestionTitlesThePane). The pane-title
/// part reads mighty-core's `PaneTitle.AutoTitle` port; `PaneTitle.Requested` (the send's title) is the run engine's (W2).
#[test]
fn the_question_titles_the_pane() {
    let wrapped = ask_targets::wrap("  왜 느린지 봐줘 ", target(Some("codex"))).unwrap();
    assert_eq!(ask_targets::question(&wrapped), "왜 느린지 봐줘");
    assert_eq!(ask_targets::question("/oh-my-claudecode:ask muse hi"), "/oh-my-claudecode:ask muse hi");
    assert_eq!(ask_targets::question("그냥 질문"), "그냥 질문");
    let pane = RunSession {
        id: Some("p".into()),
        workspace_id: Some("w".into()),
        logs: Some(vec![LogEntry::new("u".into(), "user", wrapped, mighty_core::time::now_o(), None)]),
        ..RunSession::default()
    };
    assert_eq!(mighty_core::normalize::auto_title(&pane, "Claude"), "왜 느린지 봐줘");
}
