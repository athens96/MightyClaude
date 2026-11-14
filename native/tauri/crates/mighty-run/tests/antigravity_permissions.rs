//! C# AntigravityPermissionVerification.cs (M/Tests/MightyCoreTests/AntigravityPermissionTests.swift): what a headless agy turn was refused,
//! the card, and the rules an allow puts in agy's settings for the follow-up turn. native/contracts/fixtures/antigravity-permissions.json is
//! the Mac's own file: its streams (recorded from agy 1.4.4 where it says so), its byte-exact settings edits and its samples are read here
//! unchanged. Every settings file is a temporary one; the user's own is never read or written. Every check reads English copy, as the C#
//! ones do (`English(...)`): this file is its own process, so its language never meets the Korean checks'.
//!
//! The checks of the phone's and the remote wire's sides of the card (`MobilePermission`, `RemoteWire*`, the permission route, a remote
//! pane's host) are W8's: DEFERRED.md.

mod common;

use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use mighty_core::json::Wire;
use mighty_run::antigravity::leases::ApplyError;
use mighty_run::antigravity::permissions::{
    self as flow, AllowGrant, DenialCollector, DeniedAction, OnceReason, PermissionBoard, PermissionDecision, PermissionRequest,
    RuleVerdict,
};
use mighty_run::antigravity::{AntigravityRuleLeases, settings};
use mighty_run::model::{RunEvent, StartRunRequest};
use mighty_run::parser::{OutputParser, ParserHooks};
use mighty_run::strings;
use serde_json::Value;

fn english() {
    strings::set_language("en");
}

fn fixture() -> Value {
    common::fixture_json("antigravity-permissions.json")
}

fn settings_fixture() -> Value {
    fixture()["settings"].clone()
}

/// The real settings' shape, the first edit's starting point.
fn real() -> String {
    settings_fixture()["edits"][0]["before"].as_str().unwrap().to_string()
}

fn strings_of(list: &Value) -> Vec<String> {
    list.as_array().unwrap().iter().map(|r| r.as_str().unwrap().to_string()).collect()
}

fn lines(stream: &Value) -> Vec<String> {
    strings_of(&stream["lines"])
}

fn stream(name: &str) -> Value {
    fixture()["streams"].as_array().unwrap().iter().find(|s| s["name"] == name).unwrap().clone()
}

fn actions_of(value: &Value) -> Vec<DeniedAction> {
    value.as_array().unwrap().iter().map(|a| DeniedAction::read(a).unwrap()).collect()
}

/// C#'s `Parse(lines, provider, cards)`: a parser of activity id `run-1` (no activity rows), its logs and the cards it gave.
fn parse(lines: &[String], provider: &str, cards: bool) -> (Vec<(String, String)>, Vec<PermissionRequest>) {
    let logs: Arc<Mutex<Vec<(String, String)>>> = Arc::default();
    let found: Arc<Mutex<Vec<PermissionRequest>>> = Arc::default();
    let (log, card) = (logs.clone(), found.clone());
    let hooks = ParserHooks {
        log: Some(Arc::new(move |kind: &str, text: &str| log.lock().unwrap().push((kind.to_string(), text.to_string())))),
        resume: Some(Arc::new(|_: &str| {})),
        antigravity_permission: cards
            .then(|| Arc::new(move |value: PermissionRequest| card.lock().unwrap().push(value)) as mighty_run::parser::AntigravityCardHook),
        ..ParserHooks::default()
    };
    let parser = OutputParser::new(provider, "run-1", hooks);
    for line in lines {
        parser.parse(line);
    }
    parser.flush();
    let logs = logs.lock().unwrap().clone();
    let found = found.lock().unwrap().clone();
    (logs, found)
}

fn grant(id: &str, temporary: bool, rules: &[&str]) -> AllowGrant {
    AllowGrant::new(id, rules, temporary)
}

// ── parsing ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// AntigravityPermissionVerification.cs:46 `EveryFixtureTurnGivesItsActions`.
#[test]
fn every_fixture_turn_gives_its_actions() {
    english();
    for stream in fixture()["streams"].as_array().unwrap() {
        let name = stream["name"].as_str().unwrap();
        let expected = actions_of(&stream["actions"]);
        let (logs, cards) = parse(&lines(stream), "gemini", true);
        if expected.is_empty() {
            assert!(
                cards.is_empty() && !logs.iter().any(|(_, text)| text.contains("Antigravity declined")),
                "{name}: a turn refused nothing leaves no card and no notice"
            );
            continue;
        }
        assert_eq!(cards.len(), 1, "{name}: one card");
        let card = &cards[0];
        assert_eq!(card.actions, expected, "{name}: the fixture's actions");
        assert_eq!(card.can_allow_always(), stream["canAllowAlways"].as_bool().unwrap(), "{name}: 항상 허용 offered or not");
        assert!(card.run_id == "run-1" && card.id.starts_with("agy-") && mighty_core::wire::identifier(&card.id), "{name}: the card's ids");
        // One short transcript line, not the old how-to notice.
        let notices: Vec<&(String, String)> =
            logs.iter().filter(|(kind, text)| kind == "system" && text.contains("Antigravity declined")).collect();
        assert!(
            notices.len() == 1 && notices[0].1.contains("card") && !notices[0].1.contains("permissions.allow"),
            "{name}: one line pointing at the card: {notices:?}"
        );
    }
}

/// AntigravityPermissionVerification.cs:70 `WithoutACardTheOlderNoticeStays`.
#[test]
fn without_a_card_the_older_notice_stays() {
    english();
    let (logs, _) = parse(&lines(&stream("command")), "gemini", false);
    assert!(
        logs.iter().any(|(kind, text)| kind == "system" && text.contains("permissions.allow") && text.contains("RunCommand")),
        "a parser nobody listens to for cards keeps the older notice"
    );
}

/// AntigravityPermissionVerification.cs:77 `TwoTurnsInOneRunGiveTwoCardsAndNothingCarriesOver`.
#[test]
fn two_turns_in_one_run_give_two_cards_and_nothing_carries_over() {
    english();
    let command = lines(&stream("command"));
    let clean = lines(&stream("noDenial"));
    let all: Vec<String> = command.iter().chain(&clean).chain(&command).cloned().collect();
    let (_, cards) = parse(&all, "gemini", true);
    assert!(
        cards.len() == 2 && cards[0].id != cards[1].id && cards.iter().all(|c| c.actions.len() == 1),
        "two refused turns give two cards, the clean one between none"
    );
    assert!(parse(&command, "claude", true).1.is_empty(), "another provider never builds a card");
}

/// AntigravityPermissionVerification.cs:86 `RulesAreWellFormedOrAbsent`.
#[test]
fn rules_are_well_formed_or_absent() {
    english();
    assert!(
        flow::valid_rule("command(git status)")
            && flow::valid_rule("write_file(/tmp/a b.txt)")
            && flow::valid_rule("read_url(example.com)"),
        "rules agy writes are rules"
    );
    let long = format!("command({})", "x".repeat(4_096));
    for bad in ["command()", "Command(ls)", "command(ls", "(ls)", "command(a\nb)", "command(\0)", long.as_str()] {
        assert!(!flow::valid_rule(bad), "not a rule: {}", &bad[..bad.len().min(20)]);
    }
    assert_eq!(
        flow::named_rule("x: user denied permission for write_file(/a).\nmore").as_deref(),
        Some("write_file(/a)"),
        "a named rule ends its sentence"
    );
    assert_eq!(flow::named_rule("user denied permission for something"), None, "words that are no rule name none");
    let exit = serde_json::json!({"error": {"message": "exit status 1"}});
    assert!(
        DenialCollector::denial_message(Some(&exit)).is_none() && DenialCollector::denial_message(None).is_none(),
        "a failure that is no denial is not one"
    );
}

/// AntigravityPermissionVerification.cs:98 `WhichRulesACardMayOfferFollowsTheSharedCases`.
#[test]
fn which_rules_a_card_may_offer_follows_the_shared_cases() {
    english();
    let fixture = fixture();
    let context = strings_of(&fixture["rules"]["context"]["protected"]);
    for item in fixture["rules"]["cases"].as_array().unwrap() {
        let rule = item["rule"].as_str().unwrap();
        let verdict = item["verdict"].as_str().unwrap();
        let got = flow::verdict(rule, Some(&context), None);
        assert!(got.name().eq_ignore_ascii_case(verdict), "{rule}: {verdict}, got {}", got.name());
        let reason = flow::once_reason_of(rule, Some(&context));
        let expected = item.get("reason").and_then(Value::as_str);
        assert!(
            match (reason, expected) {
                (None, None) => true,
                (Some(reason), Some(expected)) => reason.name().eq_ignore_ascii_case(expected),
                _ => false,
            },
            "{rule}: reason {expected:?}, got {reason:?}"
        );
    }
    let tools = &fixture["tools"];
    let map = tools["map"].as_object().unwrap();
    assert!(
        map.len() == flow::TOOL_PERMISSIONS.len()
            && map.iter().all(|(tool, permission)| flow::tool_permission(tool) == permission.as_str()),
        "the closed tool mapping"
    );
    assert!(strings_of(&tools["unmapped"]).iter().all(|t| flow::tool_permission(t).is_none()), "tools outside it get no rule");
    // A grant carries no refused rule, and only rules that may be kept are kept for good.
    assert!(!grant("agy-1", true, &["command(*)"]).is_valid(), "command(*) is no grant");
    let chained = grant("agy-1", false, &["command(echo hi && date)"]);
    assert!(
        chained.is_valid()
            && !chained.keeps_only_allowed_rules()
            && grant("agy-1", true, &["command(echo hi && date)"]).keeps_only_allowed_rules(),
        "a chained command only once"
    );
}

// ── settings bytes ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// `WithoutAllow(text, dropEmpty)`: the file with its allow list (and, asked, a `permissions` left empty) taken out.
fn without_allow(text: &str, drop_empty: bool) -> Value {
    let mut node: Value = serde_json::from_str(text).unwrap();
    let object = node.as_object_mut().unwrap();
    if let Some(Value::Object(permissions)) = object.get_mut("permissions") {
        permissions.shift_remove("allow");
        if drop_empty && permissions.is_empty() {
            object.shift_remove("permissions");
        }
    }
    node
}

/// AntigravityPermissionVerification.cs:121 `SettingsEditsChangeOnlyTheAllowList`.
#[test]
fn settings_edits_change_only_the_allow_list() {
    english();
    let settings_fixture = settings_fixture();
    for edit in settings_fixture["edits"].as_array().unwrap() {
        let name = edit["name"].as_str().unwrap();
        let before = edit["before"].as_str().unwrap();
        let (added, added_rules, created) = settings::adding(&strings_of(&edit["add"]), before.as_bytes(), "s.json").unwrap();
        assert_eq!(String::from_utf8(added.clone()).unwrap(), edit["afterAdd"].as_str().unwrap(), "{name} add");
        assert_eq!(added_rules, strings_of(&edit["added"]), "{name} added");
        assert_eq!(created, strings_of(&edit["created"]), "{name} created");
        let (removed, _) = settings::removing(&strings_of(&edit["remove"]), &added, "s.json").unwrap();
        assert_eq!(String::from_utf8(removed.clone()).unwrap(), edit["afterRemove"].as_str().unwrap(), "{name} remove");
        let made: HashSet<String> = created.iter().cloned().collect();
        let (pruned, _) = settings::pruning(&made, &removed, "s.json").unwrap();
        assert_eq!(String::from_utf8(pruned).unwrap(), edit["afterPrune"].as_str().unwrap(), "{name} prune");
        // Whatever else the file held reads the same.
        let had_permissions = serde_json::from_str::<Value>(before).unwrap().get("permissions").is_some();
        assert_eq!(without_allow(before, false), without_allow(&String::from_utf8(added).unwrap(), !had_permissions), "{name} other keys");
    }
    let made = &settings_fixture["newFile"];
    assert_eq!(
        String::from_utf8(settings::new_file(&strings_of(&made["add"]))).unwrap(),
        made["after"].as_str().unwrap(),
        "a new file holds the rule alone"
    );
    // A file the app made holds nothing once its rules and their section go.
    let (emptied, _) = settings::removing(&strings_of(&made["add"]), made["after"].as_str().unwrap().as_bytes(), "s.json").unwrap();
    let created = HashSet::from([settings::CREATED_FILE.to_string()]);
    assert!(
        settings::pruning(&created, &emptied, "s.json").unwrap().1 && strings_of(&made["created"]) == [settings::CREATED_FILE],
        "a made file ends empty"
    );
}

/// AntigravityPermissionVerification.cs:153 `SettingsThatAreNotTheExpectedShapeAreRefused`.
#[test]
fn settings_that_are_not_the_expected_shape_are_refused() {
    english();
    let rule = ["command(ls)".to_string()];
    for refused in settings_fixture()["refused"].as_array().unwrap() {
        let name = refused["name"].as_str().unwrap();
        let data = refused["before"].as_str().unwrap().as_bytes();
        let error = refused["error"].as_str().unwrap();
        assert_eq!(settings::adding(&rule, data, "s.json").map(|_| ()).unwrap_err().kind, error, "{name}");
        assert_eq!(settings::removing(&rule, data, "s.json").map(|_| ()).unwrap_err().kind, error, "{name} remove");
    }
}

// ── the rules the app holds ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// C#'s `Folder`: a temporary home with agy's settings and the app's ledger in it.
struct Folder {
    root: PathBuf,
}

impl Folder {
    fn new(name: &str) -> Folder {
        Folder { root: common::temp(&format!("agy-{name}")) }
    }
    fn settings_path(&self) -> PathBuf {
        self.root.join(".gemini").join("antigravity-cli").join("settings.json")
    }
    fn ledger(&self) -> PathBuf {
        self.root.join("app").join("antigravity-temporary-rules.json")
    }
    fn make(&self, text: Option<&str>) {
        std::fs::create_dir_all(self.settings_path().parent().unwrap()).unwrap();
        if let Some(text) = text {
            std::fs::write(self.settings_path(), text).unwrap();
        }
    }
    /// The app may be writing the file this very moment; a read that meets it waits a little, as the app's own retries do.
    fn text(&self) -> Option<String> {
        for attempt in 0.. {
            if !self.settings_path().exists() {
                return None;
            }
            match std::fs::read_to_string(self.settings_path()) {
                Ok(text) => return Some(text),
                Err(_) if attempt < 40 => std::thread::sleep(Duration::from_millis(25)),
                Err(error) => panic!("{error}"),
            }
        }
        None
    }
    fn leases_with(&self, retry_ms: u64, recheck_ms: u64) -> AntigravityRuleLeases {
        AntigravityRuleLeases::new(
            &self.settings_path(),
            Some(&self.ledger()),
            Some(vec![Duration::from_millis(retry_ms), Duration::from_millis(retry_ms * 2)]),
            Some(Duration::from_millis(recheck_ms)),
        )
    }
    fn leases(&self) -> AntigravityRuleLeases {
        self.leases_with(10, 0)
    }
}

impl Drop for Folder {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

fn kind<T>(result: Result<T, ApplyError>) -> Option<String> {
    match result {
        Ok(_) => None,
        Err(ApplyError::Settings(error)) => Some(error.kind),
        Err(ApplyError::Cancelled) => Some("cancelled".into()),
    }
}

/// AntigravityPermissionVerification.cs:190 `ATemporaryRuleIsAddedRecordedAndTakenOutLeavingEveryOtherByte`.
#[test]
fn a_temporary_rule_is_added_recorded_and_taken_out_leaving_every_other_byte() {
    english();
    let folder = Folder::new("temporary");
    folder.make(Some(&real()));
    let leases = folder.leases();
    let lease = leases.apply(&grant("agy-1", true, &["command(echo hi)"])).unwrap().expect("a lease");
    assert!(folder.text().unwrap().contains("\"command(echo hi)\""), "the rule is in the settings");
    assert!(std::fs::read_to_string(folder.ledger()).unwrap().contains("command(echo hi)"), "and in the ledger");
    assert_eq!(leases.held(), [("command(echo hi)".to_string(), 1)].into_iter().collect(), "held once");
    leases.release(&lease);
    assert_eq!(folder.text().unwrap(), real(), "released: every byte as it was");
    // The ledger lets go once a second look finds the rule gone.
    leases.settled();
    assert!(!folder.ledger().exists() && leases.held().is_empty(), "then no ledger");
    leases.release(&lease);
    assert_eq!(folder.text().unwrap(), real(), "releasing twice changes nothing");
}

/// AntigravityPermissionVerification.cs:207 `ARuleTwoPanesHoldStaysUntilTheLastIsDone`.
#[test]
fn a_rule_two_panes_hold_stays_until_the_last_is_done() {
    english();
    let folder = Folder::new("two-holds");
    folder.make(Some(&real()));
    let leases = folder.leases();
    let first = leases.apply(&grant("agy-1", true, &["command(echo hi)", "command(echo hi)"])).unwrap().unwrap();
    let second = leases.apply(&grant("agy-2", true, &["command(echo hi)"])).unwrap().unwrap();
    assert!(
        leases.held()["command(echo hi)"] == 2 && folder.text().unwrap().matches("command(echo hi)").count() == 1,
        "two holds, one rule"
    );
    leases.release(&first);
    assert!(folder.text().unwrap().contains("command(echo hi)"), "still held by the second");
    leases.release(&second);
    assert_eq!(folder.text().unwrap(), real(), "gone with the last");
    leases.settled();
}

/// AntigravityPermissionVerification.cs:221 `ARuleTheSettingsAlreadyHadIsNeverRemoved`.
#[test]
fn a_rule_the_settings_already_had_is_never_removed() {
    english();
    let folder = Folder::new("already");
    folder.make(Some(&real()));
    let leases = folder.leases();
    let lease = leases.apply(&grant("agy-1", true, &["command(ls)"])).unwrap().unwrap();
    assert!(lease.rules.is_empty() && folder.text().unwrap() == real(), "a rule already there is not added");
    leases.release(&lease);
    assert_eq!(folder.text().unwrap(), real(), "nor removed");
    // Always: kept, and a temporary hold on the same rule no longer removes it.
    let held = leases.apply(&grant("agy-2", true, &["command(echo hi)"])).unwrap().unwrap();
    assert!(leases.apply(&grant("agy-3", false, &["command(echo hi)", "command(pwd)"])).unwrap().is_none(), "always has no lease");
    leases.release(&held);
    leases.settled();
    let text = folder.text().unwrap();
    assert!(text.contains("\"command(echo hi)\"") && text.contains("\"command(pwd)\""), "always keeps both");
    assert!(leases.held().is_empty() && !folder.ledger().exists(), "nothing held, nothing recorded");
    // A rule that may be allowed only once is never kept for good, and a refused one never added.
    assert_eq!(
        kind(leases.apply(&grant("agy-4", false, &["command(rm -rf build)"]))).as_deref(),
        Some("onceOnly"),
        "always for a once-only rule"
    );
    assert_eq!(kind(leases.apply(&grant("agy-4", true, &["command(*)"]))).as_deref(), Some("badRule"), "command(*)");
}

/// AntigravityPermissionVerification.cs:241 `SettingsThatCannotBeReadAreLeftAloneAndRefused`.
#[test]
fn settings_that_cannot_be_read_are_left_alone_and_refused() {
    english();
    let folder = Folder::new("unreadable");
    let echo = grant("agy-1", true, &["command(echo hi)"]);
    // No folder: agy was never set up here.
    assert_eq!(kind(folder.leases().apply(&echo)).as_deref(), Some("missingFolder"), "no folder");
    for (text, expected) in [("{ broken", "notJSON"), ("{\"permissions\": {\"allow\": 3}}", "allowNotArray")] {
        folder.make(Some(text));
        let leases = folder.leases();
        assert!(
            kind(leases.apply(&echo)).as_deref() == Some(expected) && leases.check().unwrap_err().kind == expected,
            "{text}: {expected}"
        );
        assert!(folder.text().as_deref() == Some(text) && !folder.ledger().exists(), "{text}: left as it was");
    }
    // A rule that is not one is refused before the file is read.
    assert_eq!(kind(folder.leases().apply(&grant("agy-1", true, &["rm -rf /"]))).as_deref(), Some("badRule"), "a bad rule");
}

/// AntigravityPermissionVerification.cs:259 `NoFileRuleReachesAgysOwnFolderOrTheAppsData`.
#[test]
fn no_file_rule_reaches_agys_own_folder_or_the_apps_data() {
    english();
    let folder = Folder::new("protected");
    folder.make(Some(&real()));
    let leases = folder.leases();
    let gemini = folder.root.join(".gemini");
    let text = |path: &Path| path.to_string_lossy().into_owned();
    // agy's own settings, the app's ledger, and anything above them.
    let up = format!(
        "{}{}..{}.gemini{}x",
        text(&folder.root.join("project")),
        std::path::MAIN_SEPARATOR,
        std::path::MAIN_SEPARATOR,
        std::path::MAIN_SEPARATOR
    );
    for path in [
        text(&folder.settings_path()),
        text(&gemini),
        text(&folder.ledger()),
        text(folder.ledger().parent().unwrap()),
        text(&folder.root),
        up,
    ] {
        for name in ["write_file", "read_file"] {
            let rule = format!("{name}({path})");
            assert_eq!(kind(leases.apply(&grant("agy-1", true, &[&rule]))).as_deref(), Some("badRule"), "{rule}");
        }
    }
    // Through a link to it too (a symbolic link needs a privilege on Windows).
    #[cfg(unix)]
    {
        let link = folder.root.join("linked");
        std::os::unix::fs::symlink(&gemini, &link).unwrap();
        let rule = format!("write_file({})", text(&link.join("antigravity-cli").join("settings.json")));
        assert_eq!(kind(leases.apply(&grant("agy-1", true, &[&rule]))).as_deref(), Some("badRule"), "through a link");
    }
    assert_eq!(folder.text().unwrap(), real(), "nothing was added");
    // A file beside them is fine.
    let beside = format!("write_file({})", text(&folder.root.join("project").join("a.txt")));
    let lease = leases.apply(&grant("agy-2", true, &[&beside])).unwrap().unwrap();
    leases.release(&lease);
    leases.settled();
    // The real profile's .gemini and the app's own data folder are never a rule's, wherever the leases point.
    let home = flow::user_profile().unwrap();
    let real_settings = text(&home.join(".gemini").join("antigravity-cli").join("settings.json"));
    assert_eq!(flow::verdict(&format!("write_file({real_settings})"), None, None), RuleVerdict::Refused, "the real .gemini");
    assert_eq!(flow::verdict(&format!("read_file({})", text(&home)), None, None), RuleVerdict::Refused, "the profile folder");
    let saved = flow::app_data_folder();
    flow::set_app_data_folder(folder.ledger().parent().map(Path::to_path_buf));
    let refused = flow::verdict(&format!("write_file({})", text(&folder.ledger())), None, None);
    flow::set_app_data_folder(saved);
    assert_eq!(refused, RuleVerdict::Refused, "the app's data folder");
    assert_eq!(
        flow::verdict(&format!("command(cat {real_settings})"), None, None),
        RuleVerdict::Once,
        "a command naming .gemini runs once"
    );
}

/// AntigravityPermissionVerification.cs:289 `AMissingFileIsCreatedWithTheRuleAloneAndGoesWithIt`.
#[test]
fn a_missing_file_is_created_with_the_rule_alone_and_goes_with_it() {
    english();
    let folder = Folder::new("missing");
    folder.make(None);
    let leases = folder.leases();
    let lease = leases.apply(&grant("agy-1", true, &["command(echo hi)"])).unwrap().unwrap();
    assert_eq!(folder.text().as_deref(), settings_fixture()["newFile"]["after"].as_str(), "the new file");
    leases.release(&lease);
    leases.settled();
    // The file the app made ends empty, so it goes; nothing else was left in the folder.
    let entries = std::fs::read_dir(folder.settings_path().parent().unwrap()).unwrap().count();
    assert!(folder.text().is_none() && entries == 0 && !folder.ledger().exists(), "the made file goes with its rule");
    // A section the app added to an existing file goes too, byte for byte.
    const NO_SECTION: &str = "{\r\n  \"model\": \"x\"\r\n}\r\n";
    folder.make(Some(NO_SECTION));
    let again = leases.apply(&grant("agy-2", true, &["command(echo hi)"])).unwrap().unwrap();
    assert!(folder.text().unwrap().contains("\r\n    \"allow\": [\r\n"), "CRLF stays CRLF");
    leases.release(&again);
    leases.settled();
    assert_eq!(folder.text().as_deref(), Some(NO_SECTION), "the section goes");
}

/// Makes the settings unwritable for a while: on Windows the file is held open as agy may hold it (the swap fails), elsewhere its folder
/// is read-only (C#'s `Blocked`).
struct Blocked {
    #[cfg(windows)]
    _file: std::fs::File,
    #[cfg(unix)]
    folder: PathBuf,
}

impl Blocked {
    fn new(folder: &Folder) -> Blocked {
        #[cfg(windows)]
        {
            use std::os::windows::fs::OpenOptionsExt;
            let file = std::fs::OpenOptions::new().read(true).share_mode(0).open(folder.settings_path()).unwrap();
            Blocked { _file: file }
        }
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            let directory = folder.settings_path().parent().unwrap().to_path_buf();
            std::fs::set_permissions(&directory, std::fs::Permissions::from_mode(0o500)).unwrap();
            Blocked { folder: directory }
        }
    }
}

#[cfg(unix)]
impl Drop for Blocked {
    fn drop(&mut self) {
        use std::os::unix::fs::PermissionsExt;
        let _ = std::fs::set_permissions(&self.folder, std::fs::Permissions::from_mode(0o700));
    }
}

/// AntigravityPermissionVerification.cs:318 `AFailedRemovalIsTriedAgainAndKeptInTheLedgerUntilItWorks`.
#[test]
fn a_failed_removal_is_tried_again_and_kept_in_the_ledger_until_it_works() {
    english();
    let folder = Folder::new("failed-removal");
    folder.make(Some(&real()));
    let leases = folder.leases_with(20, 0);
    let first = leases.apply(&grant("agy-1", true, &["command(echo hi)"])).unwrap().unwrap();
    // The settings cannot be written: every try fails, and the ledger keeps the rule.
    {
        let _blocked = Blocked::new(&folder);
        leases.release(&first);
        leases.settled();
    }
    assert!(
        folder.text().unwrap().contains("command(echo hi)")
            && std::fs::read_to_string(folder.ledger()).unwrap().contains("command(echo hi)"),
        "kept in the settings and the ledger"
    );
    // The next release tries it again.
    let other = leases.apply(&grant("agy-2", true, &["command(pwd)"])).unwrap().unwrap();
    leases.release(&other);
    leases.settled();
    assert!(folder.text().unwrap() == real() && !folder.ledger().exists(), "taken out on the next try");
    // A pause between tries: the settings come back during it, and the removal goes through.
    let slow = folder.leases_with(400, 0);
    let third = slow.apply(&grant("agy-3", true, &["command(echo hi)"])).unwrap().unwrap();
    let block = Blocked::new(&folder);
    let reopen = std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(150));
        drop(block);
    });
    slow.release(&third);
    reopen.join().unwrap();
    slow.settled();
    assert!(folder.text().unwrap() == real() && !folder.ledger().exists(), "a later try went through");
}

/// AntigravityPermissionVerification.cs:340 `ARuleAgyWritesBackIsTakenOutAgain`.
#[test]
fn a_rule_agy_writes_back_is_taken_out_again() {
    english();
    let folder = Folder::new("written-back");
    folder.make(Some(&real()));
    let leases = folder.leases_with(10, 300);
    let lease = leases.apply(&grant("agy-1", true, &["command(echo hi)"])).unwrap().unwrap();
    let with_rule = folder.text().unwrap();
    leases.release(&lease);
    assert_eq!(folder.text().unwrap(), real(), "removed");
    // A running agy saves the settings it read earlier, the rule with them.
    std::fs::write(folder.settings_path(), &with_rule).unwrap();
    assert!(folder.ledger().exists(), "the ledger still holds it");
    leases.settled();
    assert!(folder.text().unwrap() == real() && !folder.ledger().exists(), "taken out again");
}

/// AntigravityPermissionVerification.cs:355 `AfterQuitNothingIsAddedAndALinkToNothingIsLeftAlone`.
#[test]
fn after_quit_nothing_is_added_and_a_link_to_nothing_is_left_alone() {
    english();
    let folder = Folder::new("after-quit");
    folder.make(Some(&real()));
    let leases = folder.leases();
    leases.release_all();
    assert_eq!(kind(leases.apply(&grant("agy-1", true, &["command(echo hi)"]))).as_deref(), Some("cancelled"), "applied after quit");
    assert_eq!(folder.text().unwrap(), real(), "nothing added after quit");
    // A dangling link is refused, not replaced (a symbolic link needs a privilege on Windows).
    #[cfg(unix)]
    {
        std::fs::remove_file(folder.settings_path()).unwrap();
        std::os::unix::fs::symlink(folder.root.join("gone").join("settings.json"), folder.settings_path()).unwrap();
        assert_eq!(
            kind(folder.leases().apply(&grant("agy-1", true, &["command(echo hi)"]))).as_deref(),
            Some("unreadable"),
            "a link to nothing"
        );
        assert!(std::fs::symlink_metadata(folder.settings_path()).unwrap().file_type().is_symlink(), "still the link");
    }
}

/// AntigravityPermissionVerification.cs:374 `WhatACrashLeftIsRemovedAtTheNextLaunch`.
#[test]
fn what_a_crash_left_is_removed_at_the_next_launch() {
    english();
    let folder = Folder::new("crash");
    folder.make(Some(&real()));
    // The app ends here without releasing.
    folder.leases().apply(&grant("agy-1", true, &["command(echo hi)", "command(ls)"])).unwrap();
    assert!(folder.text().unwrap().contains("command(echo hi)"), "left behind");
    let next = folder.leases();
    next.remove_leftovers();
    next.settled();
    assert!(folder.text().unwrap() == real() && !folder.ledger().exists(), "the next launch takes it out");
    // A ledger whose settings cannot be read is kept for a later launch.
    folder.leases().apply(&grant("agy-2", true, &["command(echo hi)"])).unwrap();
    let saved = folder.text().unwrap();
    std::fs::write(folder.settings_path(), "{ broken").unwrap();
    folder.leases().remove_leftovers();
    assert!(folder.ledger().exists(), "kept while the settings cannot be read");
    std::fs::write(folder.settings_path(), saved).unwrap();
    let last = folder.leases();
    last.remove_leftovers();
    last.settled();
    assert!(folder.text().unwrap() == real() && !folder.ledger().exists(), "taken out once they can");
}

/// AntigravityPermissionVerification.cs:393 `QuitReleasesEverythingAndALinkedFileStaysLinked`.
#[test]
fn quit_releases_everything_and_a_linked_file_stays_linked() {
    english();
    let folder = Folder::new("quit");
    folder.make(None);
    // A symbolic link needs a privilege on Windows: there the file itself stands in.
    #[cfg(unix)]
    let target = {
        use std::os::unix::fs::PermissionsExt;
        let target = folder.root.join("dotfiles").join("agy-settings.json");
        std::fs::create_dir_all(target.parent().unwrap()).unwrap();
        std::fs::write(&target, real()).unwrap();
        std::fs::set_permissions(&target, std::fs::Permissions::from_mode(0o600)).unwrap();
        std::os::unix::fs::symlink(&target, folder.settings_path()).unwrap();
        target
    };
    #[cfg(windows)]
    let target = {
        std::fs::write(folder.settings_path(), real()).unwrap();
        folder.settings_path()
    };
    let leases = folder.leases();
    leases.apply(&grant("agy-1", true, &["command(echo hi)"])).unwrap();
    leases.apply(&grant("agy-2", true, &["command(pwd)"])).unwrap();
    assert!(std::fs::read_to_string(&target).unwrap().contains("command(pwd)"), "written where the link points");
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        assert!(std::fs::symlink_metadata(folder.settings_path()).unwrap().file_type().is_symlink(), "the link is still a link");
        assert_eq!(std::fs::metadata(&target).unwrap().permissions().mode() & 0o777, 0o600, "its mode is kept");
    }
    leases.release_all();
    assert!(std::fs::read_to_string(&target).unwrap() == real() && leases.held().is_empty(), "quit takes every temporary rule out");
    leases.settled();
    assert!(!folder.ledger().exists(), "then no ledger");
}

/// The byte-exact edits on real files, as C#'s `AntigravitySettingsText` makes them through `AntigravityRuleLeases`: a rule added in the
/// style of its list (LF, CRLF, tabs, one line, odd spacing), every other byte kept (non-ASCII text, duplicate keys), and taken out again
/// to the very bytes the file had; a byte order mark, a comment or a trailing comma is not JSON as `JsonDocument` reads it, and such a
/// file is left as it is.
#[test]
fn settings_edits_on_disk_keep_every_other_byte() {
    english();
    let cases: [(&str, &str, &str); 6] = [
        (
            "lf",
            "{\n    \"a\": 1,\n    \"permissions\": {\n        \"allow\": [\n            \"command(ls)\"\n        ]\n    }\n}\n",
            "{\n    \"a\": 1,\n    \"permissions\": {\n        \"allow\": [\n            \"command(ls)\",\n            \"command(echo hi)\"\n        ]\n    }\n}\n",
        ),
        (
            "crlf-tabs-new-section",
            "{\r\n\t\"model\": \"x\"\r\n}\r\n",
            "{\r\n\t\"model\": \"x\",\r\n\t\"permissions\": {\r\n\t\t\"allow\": [\r\n\t\t\t\"command(echo hi)\"\r\n\t\t]\r\n\t}\r\n}\r\n",
        ),
        ("one-line", "{\"permissions\": {\"allow\": []}}", "{\"permissions\": {\"allow\": [\"command(echo hi)\"]}}"),
        (
            "odd-spacing",
            "{ \"permissions\" :{ \"allow\" :[ \"command(ls)\" , \"command(pwd)\" ] } }",
            "{ \"permissions\" :{ \"allow\" :[ \"command(ls)\" , \"command(pwd)\", \"command(echo hi)\" ] } }",
        ),
        (
            "non-ascii",
            "{\"theme\":\"한글 ✓ \\u00e9\",\"permissions\":{\"allow\":[]}}",
            "{\"theme\":\"한글 ✓ \\u00e9\",\"permissions\":{\"allow\":[\"command(echo hi)\"]}}",
        ),
        (
            "duplicate-keys",
            "{\"permissions\": {\"allow\": [\"command(ls)\"]}, \"permissions\": {\"allow\": []}}",
            "{\"permissions\": {\"allow\": [\"command(ls)\"]}, \"permissions\": {\"allow\": [\"command(echo hi)\"]}}",
        ),
    ];
    for (name, before, during) in cases {
        let folder = Folder::new(&format!("bytes-{name}"));
        folder.make(Some(before));
        let leases = folder.leases();
        let lease = leases.apply(&grant("agy-1", true, &["command(echo hi)"])).unwrap().expect("a lease");
        assert_eq!(std::fs::read(folder.settings_path()).unwrap(), during.as_bytes(), "{name}: added in the list's style");
        leases.release(&lease);
        leases.settled();
        assert_eq!(std::fs::read(folder.settings_path()).unwrap(), before.as_bytes(), "{name}: every byte as it was");
        assert!(!folder.ledger().exists(), "{name}: no ledger left");
    }
    for (name, text) in [
        ("bom", "\u{feff}{\"permissions\": {\"allow\": []}}"),
        ("line-comment", "{\n  // agy's own\n  \"permissions\": {\"allow\": []}\n}\n"),
        ("block-comment", "{\"permissions\": {\"allow\": [] /* none */}}"),
        ("trailing-comma", "{\"permissions\": {\"allow\": [\"command(ls)\",]}}"),
    ] {
        let folder = Folder::new(&format!("bytes-{name}"));
        folder.make(Some(text));
        let leases = folder.leases();
        assert_eq!(kind(leases.apply(&grant("agy-1", true, &["command(echo hi)"]))).as_deref(), Some("notJSON"), "{name}");
        assert!(std::fs::read(folder.settings_path()).unwrap() == text.as_bytes() && !folder.ledger().exists(), "{name}: left as it was");
    }
}

// ── the card ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

fn card() -> PermissionRequest {
    PermissionRequest {
        id: "agy-card".into(),
        run_id: "run-1".into(),
        actions: vec![
            DeniedAction::new(
                "command",
                "command",
                "RunCommand",
                Some("curl -s https://example.com"),
                Some("command(curl -s https://example.com)"),
            ),
            DeniedAction::new("other", "notebook_execution", "NotebookExecution", None, None),
        ],
        received_at: Some("2026-10-22T00:00:00Z".into()),
    }
}

/// AntigravityPermissionVerification.cs:427 `ACardStaysUntilALaterRunEndsOrItIsAnswered`.
#[test]
fn a_card_stays_until_a_later_run_ends_or_it_is_answered() {
    let card = card();
    let mut board = PermissionBoard::new();
    board.received(card.clone(), "pane");
    // The run that refused it ends: the card stays.
    board.run_ended("pane");
    assert!(
        board.get("pane") == Some(&card)
            && board.pending("pane", "agy-card", "run-1") == Some(&card)
            && board.pending("pane", "agy-card", "run-2").is_none(),
        "the card waits for its answer"
    );
    // A later run of the pane ends without one: it goes.
    board.run_ended("pane");
    assert!(board.get("pane").is_none(), "a later run's end takes it");
    // A later run's own card replaces it.
    board.received(card.clone(), "pane");
    board.run_ended("pane");
    let next = PermissionRequest { id: "agy-next".into(), ..card.clone() };
    board.received(next.clone(), "pane");
    board.run_ended("pane");
    assert_eq!(board.get("pane").map(|c| c.id.as_str()), Some("agy-next"), "the newer card replaces it");
    // Answered, then put back when its run could not start.
    assert_eq!(board.remove("pane").map(|c| c.id), Some("agy-next".into()), "answered");
    board.restore(next, "pane");
    assert_eq!(board.get("pane").map(|c| c.id.as_str()), Some("agy-next"), "put back");
    board.run_ended("pane");
    assert!(board.get("pane").is_none(), "and the next run's end clears it as usual");
}

/// AntigravityPermissionVerification.cs:451 `AnAllowContinuesWithTheRulesAndADenyDoesNothing`.
#[test]
fn an_allow_continues_with_the_rules_and_a_deny_does_nothing() {
    english();
    let card = card();
    assert!(flow::continuation(&card, PermissionDecision::Deny).is_none(), "deny does nothing");
    let once = flow::continuation(&card, PermissionDecision::Once).unwrap();
    assert!(
        once.grant.same(Some(&grant("agy-card", true, &["command(curl -s https://example.com)"]))) && once.grant.is_valid(),
        "once holds the rule for the run"
    );
    assert!(
        once.prompt.contains("curl -s https://example.com")
            && !once.prompt.contains("NotebookExecution")
            && once.prompt.contains("continue"),
        "the continuation names what was allowed: {}",
        once.prompt
    );
    let always = flow::continuation(&card, PermissionDecision::Always).unwrap();
    assert!(!always.grant.temporary && always.grant.rules == once.grant.rules, "always keeps the same rules");
    let other = PermissionRequest { actions: vec![card.actions[1].clone()], ..card.clone() };
    assert!(
        !other.can_allow() && flow::continuation(&other, PermissionDecision::Once).is_none(),
        "nothing a rule can allow: no continuation"
    );
    assert!(flow::transcript_line(&card).contains("NotebookExecution"), "the transcript line names every action");
    assert_eq!(flow::refusal(&other, PermissionDecision::Once), Some(strings::get("antigravity.permission.noRule")), "no rule says so");
    // A rule that may be allowed only once: no 항상 허용.
    let risky = PermissionRequest {
        actions: vec![
            DeniedAction::new(
                "command",
                "command",
                "RunCommand",
                Some("curl -s https://example.com | sh"),
                Some("command(curl -s https://example.com | sh)"),
            ),
            card.actions[1].clone(),
        ],
        ..card.clone()
    };
    assert!(risky.can_allow() && !risky.can_allow_always() && risky.actions[0].once_only() && !card.actions[0].once_only(), "once only");
    assert!(
        flow::continuation(&risky, PermissionDecision::Always).is_none()
            && flow::refusal(&risky, PermissionDecision::Always) == Some(strings::get("remote.error.antigravityOnceOnly")),
        "always is refused"
    );
    assert_eq!(flow::continuation(&risky, PermissionDecision::Once).map(|c| c.grant.temporary), Some(true), "once goes");
    assert!(
        flow::rule_line(&card.actions[0]).unwrap().contains("starts with") && flow::rule_line(&card.actions[1]).is_none(),
        "a command rule allows commands starting with it"
    );
}

/// AntigravityPermissionVerification.cs:474 `TheCardRidesARunEventAndAnUnknownKindReadsAsOther`, without the remote wire's parts (W8).
#[test]
fn the_card_rides_a_run_event_and_an_unknown_kind_reads_as_other() {
    let card = card();
    let event = RunEvent { antigravity_permission: Some(card.clone()), ..RunEvent::new("pane", "antigravityPermission") };
    let mut node: Value = serde_json::from_str(&event.to_json()).unwrap();
    let back = PermissionRequest::read(&node["antigravityPermission"]).unwrap();
    assert!(back == card && event.valid(), "the card travels and reads back");
    node["antigravityPermission"]["actions"][0]["kind"] = Value::from("network");
    assert_eq!(PermissionRequest::read(&node["antigravityPermission"]).unwrap().actions[0].kind, "other", "an unknown kind reads as other");
    assert!(flow::valid_card(&card), "the card is in bounds");
    let with_rule = |rule: &str| {
        let mut actions = card.actions.clone();
        actions[0].rule = Some(rule.into());
        RunEvent {
            antigravity_permission: Some(PermissionRequest { actions, ..card.clone() }),
            ..RunEvent::new("pane", "antigravityPermission")
        }
    };
    assert!(!with_rule("rm -rf /").valid(), "a malformed rule is refused");
    assert!(!with_rule("command(*)").valid(), "a refused rule is refused");
    let empty = RunEvent {
        antigravity_permission: Some(PermissionRequest { actions: vec![], ..card.clone() }),
        ..RunEvent::new("pane", "antigravityPermission")
    };
    assert!(!empty.valid(), "an empty card is refused");
    // A run start carries the grant beside the request, never saved with it (the request has no JSON here at all).
    let mut request = StartRunRequest::new("pane", "ws", "claude", "go on").with_provider("gemini");
    request.antigravity_grant = Some(grant("agy-card", true, &["command(ls)"]));
    assert!(request.validate().unwrap().antigravity_grant.is_some(), "kept through validation");
}

/// AntigravityPermissionVerification.cs:534 `ThePhoneSampleIsWhatTheMacSends`, the card's own words: each once-only reason, an interpreter
/// alone, and the answers' decisions. The phone's permission itself (`MobilePermission`, `MobileAntigravityCard`) is W8's.
#[test]
fn the_phone_sample_is_what_this_host_sends() {
    english();
    let mobile = fixture()["mobile"].clone();
    let reasons = mobile["onceOnlyReasons"].as_object().unwrap();
    assert_eq!(reasons.len(), OnceReason::ALL.len(), "every reason has words");
    for reason in OnceReason::ALL {
        let name = reason.name();
        let camel = format!("{}{}", name[..1].to_lowercase(), &name[1..]);
        let key = reasons[&camel].as_str().unwrap();
        assert!(flow::once_only_reason_text(reason) == strings::get(key) && !strings::get(key).starts_with("antigravity."), "{name} words");
    }
    let interpreter = DeniedAction::new("command", "command", "RunCommand", Some("bash"), Some("command(bash)"));
    assert!(
        interpreter.once_only_reason() == Some(OnceReason::Interpreter)
            && flow::once_only_text(&interpreter) == Some(strings::get("antigravity.permission.onceOnlyInterpreter")),
        "bash alone"
    );
    for answer in mobile["answers"].as_array().unwrap() {
        let body = &answer["body"];
        let allow = body["allow"].as_bool().unwrap();
        let always = body.get("always").and_then(Value::as_bool) == Some(true);
        match answer.get("decision").and_then(Value::as_str) {
            Some(decision) => {
                let named = match flow::decision(allow, always) {
                    PermissionDecision::Once => "once",
                    PermissionDecision::Always => "always",
                    PermissionDecision::Deny => "deny",
                };
                assert_eq!(named, decision, "answer {decision}");
            }
            None => assert!(always && !allow && answer["status"] == 400, "always without allow is refused"),
        }
    }
}

// ── runs (the fake agy through the Windows ProcessHost) ─────────────────────────────────────────────────────────────────────────

#[cfg(windows)]
mod runs {
    use super::*;
    use mighty_core::model::{AppSnapshot, RunSession, Workspace};
    use mighty_run::RunManager;
    use mighty_run::RunManagerOptions;
    use mighty_run::manager::ResolveWorkspace;
    use mighty_run::service::{RunService, RunServiceOptions};

    const CONVERSATION: &str = "11111111-2222-4333-8444-555555555555";

    /// `FakeAgyCatalog`: every provider is the fake agy refused `echo hi` on a new conversation.
    fn catalog(folder: &Folder) -> Arc<mighty_run::providers::ProviderCatalog> {
        let record = folder.root.join("agy-record");
        common::fake_catalog(&[
            "--fake-cli",
            "gemini",
            &record.to_string_lossy(),
            "--agy-denied",
            &folder.settings_path().to_string_lossy(),
        ])
    }

    fn ended(value: &RunEvent) -> bool {
        value.kind == "status" && matches!(value.status.as_deref(), Some("completed" | "error" | "stopped"))
    }

    /// AntigravityPermissionVerification.cs:590 `TheRunnerSendsTheCardBeforeTheRunsEnd`.
    #[tokio::test(flavor = "multi_thread")]
    async fn the_runner_sends_the_card_before_the_runs_end() {
        english();
        let folder = Folder::new("runner-card");
        folder.make(Some(&real()));
        let workspace = Workspace { path: Some(folder.root.to_string_lossy().into_owned()), ..Workspace::default() };
        let events: Arc<Mutex<Vec<RunEvent>>> = Arc::default();
        let sink = events.clone();
        let resolve: ResolveWorkspace = {
            let workspace = workspace.clone();
            Arc::new(move |_| {
                let workspace = workspace.clone();
                Box::pin(async move { Ok(workspace) })
            })
        };
        let manager =
            RunManager::new(catalog(&folder), RunManagerOptions::new("", resolve, Arc::new(move |e| sink.lock().unwrap().push(e))));
        manager
            .start(StartRunRequest::new("pane", workspace.id.as_deref().unwrap(), "claude", "say hi").with_provider("gemini"))
            .await
            .unwrap();
        common::until(|| events.lock().unwrap().iter().any(ended), 20000, "the run ends").await;
        manager.dispose().await;
        let values = events.lock().unwrap().clone();
        let card = values.iter().position(|e| e.kind == "antigravityPermission");
        let end = values.iter().position(|e| e.kind == "status" && e.status.as_deref() == Some("completed"));
        assert!(
            matches!((card, end), (Some(card), Some(end)) if end > card),
            "the card comes before the run's end: {:?}",
            values.iter().map(|e| format!("{}:{:?}", e.kind, e.status)).collect::<Vec<_>>()
        );
        let event = &values[card.unwrap()];
        let sent = event.antigravity_permission.as_ref().unwrap();
        assert!(
            sent.rules() == ["command(echo hi)"] && sent.actions[0].display_name == "RunCommand" && event.valid(),
            "the card names the command"
        );
        assert_eq!(folder.text().unwrap(), real(), "a run alone never touches the settings");
    }

    /// C#'s `Desktop(folder, catalog)`: a service over a workspace with one Antigravity pane, its rules in the folder's settings and its
    /// ledger in the folder's `state`.
    fn desktop(folder: &Folder) -> (Arc<RunService>, String, String) {
        let project = folder.root.join("project");
        std::fs::create_dir_all(&project).unwrap();
        let workspace = Workspace { path: Some(project.to_string_lossy().into_owned()), ..Workspace::default() };
        let pane = RunSession {
            id: Some("agy-pane".into()),
            workspace_id: workspace.id.clone(),
            kind: Some("claude".into()),
            provider: Some("gemini".into()),
            title: Some("Antigravity".into()),
            ..RunSession::default()
        };
        let snapshot =
            AppSnapshot { workspaces: Some(vec![workspace.clone()]), sessions: Some(vec![Arc::new(pane)]), ..AppSnapshot::default() };
        let rules = AntigravityRuleLeases::new(
            &folder.settings_path(),
            Some(&folder.root.join("state").join("antigravity-temporary-rules.json")),
            None,
            None,
        );
        let configure: mighty_run::service::Configure = Box::new(move |options| options.antigravity_rules = Some(rules));
        let (service, events) = RunService::new(
            snapshot,
            catalog(folder),
            RunServiceOptions {
                plugin_directory: String::new(),
                permission_card: false,
                remote: None,
                configure: Some(configure),
                admitting: None,
            },
        );
        drop(events);
        (Arc::new(service), "agy-pane".into(), workspace.id.clone().unwrap())
    }

    fn pane(service: &RunService) -> Arc<RunSession> {
        service.snapshot().session("agy-pane").cloned().unwrap()
    }

    /// `Runs(service, pane)`: the requests the pane sent.
    fn runs(service: &RunService) -> usize {
        pane(service).logs().iter().filter(|l| l.kind() == "user").count()
    }

    /// `FirstTurn`: a first turn that is refused "echo hi"; its card waits past its run's end.
    async fn first_turn(service: &RunService, workspace: &str, count: usize) -> PermissionRequest {
        // A new conversation: the fake agy refuses only a turn it was not asked to continue.
        service.start(StartRunRequest::new("agy-pane", workspace, "claude", "say hi").with_provider("gemini")).await.unwrap();
        common::until(
            || runs(service) == count && pane(service).status() == "completed" && service.antigravity_card("agy-pane").is_some(),
            20000,
            "the first turn's card",
        )
        .await;
        let card = service.antigravity_card("agy-pane").unwrap();
        assert_eq!(card.rules(), ["command(echo hi)"], "the card names the refused command");
        assert!(
            pane(service).logs().iter().any(|l| l.kind() == "system" && l.text() == flow::transcript_line(&card)),
            "the transcript says a card waits"
        );
        card
    }

    fn continuing(workspace: &str, prompt: &str, grant: AllowGrant, card: Option<PermissionRequest>) -> StartRunRequest {
        let mut request = StartRunRequest::new("agy-pane", workspace, "claude", prompt).with_provider("gemini");
        request.resume_id = Some(CONVERSATION.into());
        request.antigravity_grant = Some(grant);
        request.antigravity_card = card;
        request
    }

    /// AntigravityPermissionVerification.cs:611 `APanesContinuationHoldsTheRuleForItsRunAndKeepsTheTitle`.
    #[tokio::test(flavor = "multi_thread")]
    async fn a_panes_continuation_holds_the_rule_for_its_run_and_keeps_the_title() {
        english();
        let folder = Folder::new("continuation");
        folder.make(Some(&real()));
        let (service, pane_id, workspace) = desktop(&folder);
        let card = first_turn(&service, &workspace, 1).await;
        let title = pane(&service).title.clone();
        assert_eq!(pane(&service).resume_id.as_deref(), Some(CONVERSATION), "the conversation to continue");
        let once = flow::continuation(&card, PermissionDecision::Once).unwrap();
        assert!(
            service.take_antigravity_card(&pane_id) == Some(card.clone()) && service.antigravity_card(&pane_id).is_none(),
            "answered: the card goes"
        );
        service.start_reached(continuing(&workspace, &once.prompt, once.grant, None)).await.unwrap();
        common::until(|| runs(&service) == 2 && pane(&service).status() == "completed", 20000, "the continuation ends").await;
        let logs = pane(&service).logs().to_vec();
        assert!(
            logs.iter().any(|l| l.kind() == "assistant" && l.text() == "allowed"),
            "the continuation ran with the rule in place: {:?}",
            logs.iter().map(|l| format!("{}:{}", l.kind(), l.text())).collect::<Vec<_>>()
        );
        common::until(|| folder.text().as_deref() == Some(real().as_str()), 10000, "the rule leaves with its run").await;
        // The ledger lets go once a second look a little later finds the rule gone.
        let rules = service.antigravity_rules().clone();
        tokio::task::spawn_blocking(move || rules.settled()).await.unwrap();
        assert!(
            !folder.root.join("state").join("antigravity-temporary-rules.json").exists() && service.antigravity_rules().held().is_empty(),
            "the rule left with its run, and the ledger with it"
        );
        assert_eq!(pane(&service).title, title, "the pane's title is not the continuation's words");
        // Always keeps the rule.
        let card = first_turn(&service, &workspace, 3).await;
        let always = flow::continuation(&card, PermissionDecision::Always).unwrap();
        service.take_antigravity_card(&pane_id);
        service.start_reached(continuing(&workspace, &always.prompt, always.grant, None)).await.unwrap();
        common::until(|| runs(&service) == 4 && pane(&service).status() == "completed", 20000, "the always continuation ends").await;
        assert!(
            folder.text().unwrap().contains("\"command(echo hi)\"") && service.antigravity_rules().held().is_empty(),
            "always keeps the rule for good"
        );
        service.dispose().await;
    }

    /// AntigravityPermissionVerification.cs:643 `AContinuationTheSettingsRefuseNeverStarts`.
    #[tokio::test(flavor = "multi_thread")]
    async fn a_continuation_the_settings_refuse_never_starts() {
        english();
        let folder = Folder::new("refused-start");
        folder.make(Some(&real()));
        let (service, pane_id, workspace) = desktop(&folder);
        let card = first_turn(&service, &workspace, 1).await;
        std::fs::write(folder.settings_path(), "{ broken").unwrap();
        let once = flow::continuation(&card, PermissionDecision::Once).unwrap();
        assert_eq!(service.check_antigravity_settings().await.unwrap_err().kind, "notJSON", "the check reads the settings first");
        let words = strings::get_with("antigravity.settings.error.notJSON", &[("path", &folder.settings_path().to_string_lossy())]);
        let refused = service.start_reached(continuing(&workspace, &once.prompt, once.grant, None)).await;
        assert!(matches!(&refused, Err(error) if error.message() == words), "a start whose rules cannot go in place: {refused:?}");
        let session = pane(&service);
        assert!(
            session.status() == "error"
                && session.logs().iter().rev().find(|l| l.kind() == "error").map(|l| l.text()) == Some(words.as_str()),
            "the pane says why"
        );
        assert!(
            folder.text().as_deref() == Some("{ broken") && !folder.root.join("state").join("antigravity-temporary-rules.json").exists(),
            "nothing was written"
        );
        service.restore_antigravity_card(&pane_id, card.clone());
        assert_eq!(service.antigravity_card(&pane_id), Some(card), "the card can come back with the reason");
        service.dispose().await;
    }

    /// AntigravityPermissionVerification.cs:665 `AContinuationThatCannotStartBringsItsCardBack`.
    #[tokio::test(flavor = "multi_thread")]
    async fn a_continuation_that_cannot_start_brings_its_card_back() {
        english();
        let folder = Folder::new("card-back");
        folder.make(Some(&real()));
        let (service, pane_id, workspace) = desktop(&folder);
        let card = first_turn(&service, &workspace, 1).await;
        let once = flow::continuation(&card, PermissionDecision::Once).unwrap();
        service.take_antigravity_card(&pane_id);
        std::fs::write(folder.settings_path(), "{ broken").unwrap();
        let refused = service.start(continuing(&workspace, &once.prompt, once.grant, Some(card.clone()))).await;
        assert!(refused.is_err(), "a start whose rules cannot go in place");
        let words = strings::get_with("antigravity.settings.error.notJSON", &[("path", &folder.settings_path().to_string_lossy())]);
        assert!(
            service.antigravity_card(&pane_id) == Some(card) && service.antigravity_error(&pane_id) == Some(words),
            "the card is back with the reason"
        );
        service.dispose().await;
    }
}
