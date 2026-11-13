//! C# `QueueRunNowVerification` (Verification.cs:690-691), held to `native/contracts/fixtures/queue-run-now.json`, and
//! `QueuedComposerVerification.QueuePreservesOrderBoundsAndSettlementPolicy` (Verification.cs:428).

mod common;

use std::collections::HashSet;

use mighty_composer::queue::{self, ComposerSecondSend, QueueRunNowPath, QueuedComposerInput, QueuedInputBuffer, RunNowPane};
use mighty_core::graph::MightyGraphRun;

/// Verification.cs:690 "queue: 바로 실행 follows native/contracts/fixtures/queue-run-now.json" (EveryCaseDecidesAsTheFixtureSays).
/// The phone rows' part (`DelegationQueueRows.MobileItems`) is the remote crate's (W8) and is not ported here.
#[test]
fn every_case_decides_as_the_fixture_says() {
    let fixture = common::fixture("queue-run-now.json");
    let mut seen = HashSet::new();
    for item in fixture["cases"].as_array().unwrap() {
        let (row, p, label) = (&item["row"], &item["pane"], item["name"].as_str().unwrap());
        let flag = |key: &str| p[key].as_bool().unwrap();
        let text = |key: &str| p[key].as_str().unwrap();
        let second = queue::choose_second_send(
            text("kind"),
            text("provider"),
            text("mode"),
            flag("isChild"),
            flag("delegationOn"),
            flag("canHostWorktree"),
        );
        // DelegationChildChoice[] choices = otherAgentReady ? [one unblocked agent] : []
        let menu_only = queue::menu_only(
            text("kind"),
            text("provider"),
            text("mode"),
            flag("isChild"),
            flag("delegationOn"),
            flag("canHostWorktree"),
            flag("otherAgentReady"),
        );
        let pane = RunNowPane {
            busy: flag("busy"),
            held_for_update: flag("heldForUpdate"),
            second_send: second,
            menu_only,
            waits_on_background_only: flag("waitsOnBackgroundOnly"),
            held: p["held"].as_u64().unwrap() as usize,
            notice_turn_running: flag("noticeTurnRunning"),
        };
        let held_index = row["heldIndex"].as_u64().map(|i| i as usize);
        let path = queue::decide(
            held_index,
            row["text"].as_str().unwrap_or(""),
            row["attachments"].as_u64().unwrap() as usize,
            &pane,
            row["continuation"].as_bool().unwrap(),
        );
        assert_eq!(path.name(), item["expect"].as_str().unwrap(), "queue run now: {label}");
        let wire = &item["wire"];
        let expected =
            if wire.is_null() { None } else { Some((wire["runNow"].as_str().unwrap(), wire.get("runNowReason").and_then(|r| r.as_str()))) };
        assert_eq!(path.wire(), expected, "queue run now wire: {label}");
        if let Some((run_now, reason)) = path.wire() {
            assert_eq!(reason.is_none(), run_now != "unavailable", "queue run now reason: {label}");
        }
        seen.insert(path.name());
    }
    for path in QueueRunNowPath::ALL {
        assert!(seen.contains(path.name()), "the fixture has a case for {}", path.name());
    }
    let runs: Vec<&str> = QueueRunNowPath::ALL.iter().filter(|p| p.runs()).map(|p| p.name()).collect();
    assert_eq!(runs, ["worktree", "agentMenu", "here", "steer", "held"]);
    let busy = RunNowPane {
        busy: true,
        held_for_update: false,
        second_send: ComposerSecondSend::Worktree,
        menu_only: false,
        waits_on_background_only: true,
        held: 0,
        notice_turn_running: false,
    };
    assert_eq!(queue::decide(None, "Go on", 0, &busy, true), QueueRunNowPath::WaitsForTurn);
    assert_eq!(queue::decide(None, "Go on", 0, &RunNowPane { busy: false, ..busy }, true), QueueRunNowPath::Here);
    assert_eq!(
        queue::decide(None, "Go on", 0, &RunNowPane { second_send: ComposerSecondSend::Queue, menu_only: true, ..busy }, true),
        QueueRunNowPath::WaitsForTurn
    );
    let row = |continuation| QueuedComposerInput {
        id: "q".into(),
        text: "Go on".into(),
        attachments: vec![],
        permission_mode_override: None,
        prepared: false,
        continuation,
    };
    assert!(queue::is_continuation(&row(true)) && !queue::is_continuation(&row(false)));
    let l = common::ko();
    let mut buffer = QueuedInputBuffer::new();
    let earlier = buffer.add("First", vec![], None, false, false, &l).unwrap();
    let later = buffer.add("Second", vec![], None, false, false, &l).unwrap();
    assert!(buffer.move_to_front(&later.id));
    assert_eq!(buffer.items().iter().map(|i| i.id.as_str()).collect::<Vec<_>>(), [later.id.as_str(), earlier.id.as_str()]);
    assert!(!buffer.move_to_front("missing"));
    buffer.remove(&earlier.id);
    buffer.insert(0, earlier.clone());
    buffer.insert(0, earlier.clone());
    assert_eq!(buffer.items().iter().map(|i| i.id.as_str()).collect::<Vec<_>>(), [earlier.id.as_str(), later.id.as_str()]);
}

fn run(id: &str, status: &str, notice: bool) -> MightyGraphRun {
    MightyGraphRun { id: Some(id.into()), status: Some(status.into()), notice_run: notice.then_some(true), ..MightyGraphRun::default() }
}

/// Verification.cs:691 "queue: 바로 실행 never steers into a running notice turn" (ARunningNoticeTurnTakesNoSteer).
#[test]
fn a_running_notice_turn_takes_no_steer() {
    let request = run("r", "completed", false);
    let running = run("r~notice~1", "running", true);
    let answered = run("r~notice~1", "completed", true);
    assert!(queue::notice_turn_running(&[request.clone(), running.clone()]));
    assert!(!queue::notice_turn_running(&[request.clone(), answered.clone()]));
    assert!(!queue::notice_turn_running(std::slice::from_ref(&request)) && !queue::notice_turn_running(&[]));
    assert!(!queue::notice_turn_running(&[request.clone(), running.clone(), run("r2", "completed", false)]));
    assert!(queue::notice_turn_ended(&[request.clone(), running.clone()], &[request.clone(), answered.clone()]));
    assert!(!queue::notice_turn_ended(&[request.clone(), running.clone()], &[request.clone(), running.clone()]));
    assert!(!queue::notice_turn_ended(&[request.clone(), answered.clone()], &[request.clone(), answered]));
    assert!(!queue::notice_turn_ended(std::slice::from_ref(&request), &[request.clone(), running.clone()]));
    let pane = RunNowPane {
        busy: true,
        held_for_update: false,
        second_send: ComposerSecondSend::Queue,
        menu_only: false,
        waits_on_background_only: true,
        held: 0,
        notice_turn_running: queue::notice_turn_running(&[request, running]),
    };
    assert_eq!(queue::decide(None, "Next", 0, &pane, false), QueueRunNowPath::WaitsForTurn);
    assert_eq!(queue::decide(Some(0), "", 0, &pane, false), QueueRunNowPath::WaitsForTurn);
    assert_eq!(queue::decide(Some(1), "", 0, &pane, false), QueueRunNowPath::Hidden);
    let quiet = RunNowPane { notice_turn_running: false, ..pane };
    assert_eq!(queue::decide(None, "Next", 0, &quiet, false), QueueRunNowPath::Steer);
    assert_eq!(queue::decide(Some(0), "", 0, &quiet, false), QueueRunNowPath::Held);
    assert_eq!(queue::decide(Some(0), "", 0, &RunNowPane { busy: false, ..pane }, false), QueueRunNowPath::Held);
}

/// Verification.cs:428 "QueuedComposerVerification QueuePreservesOrderBoundsAndSettlementPolicy".
#[test]
fn queue_preserves_order_bounds_and_settlement_policy() {
    let l = common::ko();
    let mut queue = QueuedInputBuffer::new();
    let first = queue.add("첫 번째", vec![], None, false, false, &l).unwrap();
    let second = queue.add("second", vec![], None, false, false, &l).unwrap();
    assert!(queue.add(" ", vec![], None, false, false, &l).is_err());
    assert_eq!(queue.items().iter().map(|i| i.id.as_str()).collect::<Vec<_>>(), [first.id.as_str(), second.id.as_str()]);
    assert!(!queue.settle("running", false) && !queue.settle("error", false) && queue.items().len() == 2);
    assert!(queue.settle("completed", false) && queue.settle("idle", false));
    assert!(queue.remove(&first.id) && !queue.remove(&first.id) && queue.items()[0].id == second.id);
    let prepared = queue.add("/ouroboros:interview goal", vec![], None, true, false, &l).unwrap();
    assert!(!second.prepared && prepared.prepared && queue.remove(&prepared.id));
    while queue.items().len() < queue::MAXIMUM_ITEMS {
        queue.add("queued", vec![], None, false, false, &l).unwrap();
    }
    assert!(matches!(queue.add("overflow", vec![], None, false, false, &l), Err(queue::QueueError::Full(_))));
    assert_eq!(queue.items().len(), queue::MAXIMUM_ITEMS);
    assert!(!queue.settle("stopped", false) && queue.items().is_empty());
}

/// C# `QueuedInputBuffer.SettleNotice` (QueuedInputBuffer.cs:51), as `ComposerParityVerification.QueueSaysWhatBecameOfIt` reads it:
/// a stop cancels what waited and an error holds it, each said once; nothing for an empty queue or another end.
#[test]
fn the_queue_says_what_became_of_it() {
    let l = common::ko();
    assert_eq!(queue::settle_notice("stopped", 2, false, &l), Some(l.get_with("queue.log.cancelledOnStop", &[("count", "2")])));
    assert_eq!(queue::settle_notice("error", 1, false, &l), Some(l.get_with("queue.log.heldAfterError", &[("count", "1")])));
    assert_eq!(queue::settle_notice("stopped", 2, true, &l), None);
    assert_eq!(queue::settle_notice("completed", 2, false, &l), None);
    assert_eq!(queue::settle_notice("stopped", 0, false, &l), None);
}
