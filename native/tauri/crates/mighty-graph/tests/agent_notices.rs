//! The file the macOS AgentNoticeTests read too (native/contracts/fixtures/agent-notices.json; C# AgentNoticeVerification.cs): a background
//! sub-agent's `<task-notification>` and final report (its hand-back) settle its block the way a background command's notification does,
//! and the turn either opens is a notice turn with a run of its own, not the request's. A message the agent sends while still running only
//! joins its block's log.

mod common;

use std::collections::HashMap;

use common::*;
use mighty_core::graph::MightyGraphRun;
use mighty_core::json::Wire;
use mighty_core::locale::Locale;
use mighty_graph::notice::{self, Opener};
use mighty_graph::tracker::{self, ExecutionGraphTracker};
use mighty_graph::{ExecutionGraphNode, handback, node, support};
use serde_json::Value;

fn fixture_cases(key: &str) -> Vec<Value> {
    items(fixture("agent-notices.json").get(key))
}

fn same_list<T: PartialEq + std::fmt::Debug>(actual: Vec<T>, expected: Vec<T>, why: &str) {
    assert!(actual == expected, "{why}: expected {expected:?} but was {actual:?}");
}

#[test]
fn agent_notices_the_shared_cases_hold() {
    hand_backs_parse_as_the_fixture_says();
    notice_results_are_the_fixtures();
    streams_settle_the_block_and_open_notice_runs();
    a_mixed_turn_leaves_the_earlier_run_in_its_own_state();
    notice_titles_follow_the_fixture();
    result_titles_follow_the_fixture();
    awaiting_notice_follows_the_fixture();
}

fn hand_backs_parse_as_the_fixture_says() {
    let cases = fixture_cases("handbacks");
    check(!cases.is_empty(), "the fixture has hand-back cases");
    for item in cases {
        let name = text(&item, "name").unwrap_or("");
        let parsed = handback::parse(text(&item, "text").unwrap_or_else(|| panic!("{name}: no text")));
        let expected = items(item.get("expect"));
        same_list(
            parsed.iter().map(|m| m.from.clone()).collect(),
            expected.iter().map(|e| text(e, "from").unwrap_or("").to_string()).collect(),
            &format!("{name}: senders"),
        );
        same_list(
            parsed.iter().map(|m| m.body.clone()).collect(),
            expected.iter().map(|e| text(e, "body").unwrap_or("").to_string()).collect(),
            &format!("{name}: bodies"),
        );
        same_list(
            parsed.iter().map(|m| m.is_handback).collect(),
            expected.iter().map(|e| flag(e, "isHandback")).collect(),
            &format!("{name}: final reports"),
        );
    }
}

fn notice_results_are_the_fixtures() {
    let cases = fixture_cases("results");
    check(!cases.is_empty(), "the fixture has result cases");
    for item in cases {
        let event = item.get("event").cloned().unwrap_or(Value::Null);
        same(
            tracker::is_notification_result(&event),
            flag(&item, "expect"),
            &format!("{}: a notice turn's result", text(&item, "name").unwrap_or("")),
        );
    }
}

/// One notice run a stream opened: its id, its main block and its copies by id.
struct NoticeRun {
    id: String,
    main: Option<ExecutionGraphNode>,
    copies: Vec<ExecutionGraphNode>,
}

/// The notice runs a stream opened, in the order they were numbered.
fn notice_runs(latest: &HashMap<String, ExecutionGraphNode>) -> Vec<NoticeRun> {
    let number = |id: &str| -> i32 { id.rfind(notice::MARKER).and_then(|at| id[at + notice::MARKER.len()..].parse().ok()).unwrap_or(0) };
    let mut groups: Vec<String> = vec![];
    for node in latest.values() {
        if notice::is_notice_run(&node.run_id) && !groups.contains(&node.run_id) {
            groups.push(node.run_id.clone());
        }
    }
    groups.sort_by_key(|id| number(id));
    groups
        .into_iter()
        .map(|id| {
            let main = latest.values().find(|n| n.run_id == id && n.kind == "main").cloned();
            let mut copies: Vec<ExecutionGraphNode> = latest.values().filter(|n| n.run_id == id && n.kind != "main").cloned().collect();
            copies.sort_by(|a, b| a.id.encode_utf16().cmp(b.id.encode_utf16()));
            NoticeRun { id, main, copies }
        })
        .collect()
}

fn latest_by_id(emitted: &[ExecutionGraphNode]) -> HashMap<String, ExecutionGraphNode> {
    emitted.iter().map(|n| (n.id.clone(), n.clone())).collect()
}

fn streams_settle_the_block_and_open_notice_runs() {
    let ko = Locale::new("ko");
    let cases = fixture_cases("streams");
    check(cases.len() >= 37, &format!("the fixture has every stream case; got {}", cases.len()));
    for item in cases {
        let name = text(&item, "name").unwrap_or("").to_string();
        const RUN_ID: &str = "agent-notices";
        let (mut tracker, emitted) = recording_tracker(RUN_ID, Some("Do the request"), "claude", None);
        let events = items(item.get("events"));
        for value in &events {
            tracker.consume(value);
        }
        let expect = item.get("expect").cloned().unwrap_or(Value::Null);
        let tool = text(&item, "agentToolUseId").unwrap_or_else(|| panic!("{name}: no agentToolUseId"));
        let latest = latest_by_id(&emitted.lock().expect("nodes"));
        let block = latest.get(&node::agent_node_id(RUN_ID, tool)).cloned().unwrap_or_else(|| panic!("{name}: no block for {tool}"));
        same(Some(block.kind.as_str()), text(&expect, "agentKind"), &format!("{name}: kind"));
        same(Some(block.state.as_str()), text(&expect, "agentState"), &format!("{name}: state"));
        same(block.output.as_deref(), text(&expect, "agentOutput"), &format!("{name}: result"));
        // Started in the background, where its run writes, Claude's answer to its notice: checked when the case names them (null = absent).
        if let Some(background) = expect.get("agentBackground") {
            same(block.background, background.as_bool(), &format!("{name}: background"));
        }
        if let Some(file) = expect.get("agentOutputFile") {
            same(block.output_file.as_deref(), file.as_str(), &format!("{name}: output file"));
        }
        if let Some(reply) = expect.get("agentNoticeReply") {
            same(block.notice_reply.as_deref(), reply.as_str(), &format!("{name}: notice reply"));
        }
        if let Some(count) = expect.get("noticeReplyBlocks").and_then(Value::as_i64) {
            same(
                latest.values().filter(|n| n.notice_reply.is_some()).count() as i64,
                count,
                &format!("{name}: blocks with a notice reply"),
            );
        }
        // A message the agent sent while running is in its log, not its result.
        for wanted in strings(expect.get("agentLog")) {
            check(
                block.entries.iter().any(|e| e.kind() == "assistant" && e.text() == wanted),
                &format!("{name}: the log holds '{wanted}'"),
            );
        }
        // A message that settles the block, or that nobody here sent, is not in its log.
        for unwanted in strings(expect.get("agentLogLacks")) {
            check(!block.entries.iter().any(|e| e.text() == unwanted), &format!("{name}: the log lacks '{unwanted}'"));
        }
        let notices = latest
            .values()
            .filter(|n| n.kind == "steer" && n.input.as_deref().unwrap_or("").starts_with(support::TASK_NOTICE_PREFIX))
            .count();
        same(notices as i64, expect.get("noticeBlocks").and_then(Value::as_i64).unwrap_or(0), &format!("{name}: 작업 알림 blocks"));
        let main = latest[&node::main_node_id(RUN_ID)].clone();
        for part in strings(expect.get("mainOutputContains")) {
            check(
                main.output.as_deref().is_some_and(|o| o.contains(&part)),
                &format!("{name}: the result holds '{part}'; got '{:?}'", main.output),
            );
        }
        // No answer to a notice extends the request's result any more.
        check(
            !main.output.as_deref().is_some_and(|o| o.contains(support::NOTICE_SECTION_MARK)),
            &format!("{name}: notice section in the request's result; got '{:?}'", main.output),
        );

        // Each notice turn is a run of its own: "<run>:notice:<n>", n from 1.
        let runs = notice_runs(&latest);
        same_list(
            runs.iter().map(|r| r.id.clone()).collect(),
            (0..runs.len()).map(|index| notice::run_id(RUN_ID, index + 1)).collect(),
            &format!("{name}: run ids"),
        );
        if let Some(count) = expect.get("noticeRuns").and_then(Value::as_i64) {
            same(runs.len() as i64, count, &format!("{name}: notice runs"));
        }
        if let Some(children) = expect.get("noticeRunChildren").and_then(Value::as_array) {
            same_list(
                runs.iter().map(|r| r.copies.len() as i64).collect(),
                children.iter().map(|c| c.as_i64().unwrap_or(0)).collect(),
                &format!("{name}: copies"),
            );
        }
        if let Some(output) = expect.get("noticeRunOutput") {
            same(
                runs.first().and_then(|r| r.main.as_ref()).and_then(|m| m.output.clone()),
                output.as_str().map(str::to_string),
                &format!("{name}: notice run output"),
            );
        }
        if let Some(input) = text(&expect, "noticeRunInput") {
            same(
                runs.first().and_then(|r| r.main.as_ref()).and_then(|m| m.input.as_deref()),
                Some(input),
                &format!("{name}: notice run input"),
            );
        }
        if let Some(titles) = expect.get("noticeRunTitles").and_then(Value::as_array) {
            same_list(
                runs.iter().map(|r| r.main.as_ref().map(|m| m.title.clone()).unwrap_or_default()).collect(),
                titles.iter().map(|t| t.as_str().unwrap_or("").to_string()).collect(),
                &format!("{name}: titles"),
            );
        }
        if let Some(outputs) = expect.get("noticeRunOutputs").and_then(Value::as_array) {
            same_list(
                runs.iter().map(|r| r.main.as_ref().and_then(|m| m.output.clone()).unwrap_or_else(|| "<none>".into())).collect(),
                outputs.iter().map(|o| o.as_str().map(str::to_string).unwrap_or_else(|| "<none>".into())).collect(),
                &format!("{name}: outputs"),
            );
        }
        let kinds: Vec<String> = [Opener::Work, Opener::Message, Opener::Delegation].iter().map(|o| notice::kind(*o, &ko)).collect();
        let ends_in_turn = events.last().is_none_or(|e| text(e, "type") != Some("result"));
        for run in &runs {
            let notice = run.main.clone().unwrap_or_else(|| panic!("{name}: {} has no main", run.id));
            check(
                kinds.iter().any(|kind| notice.title == *kind || notice.title.starts_with(&format!("{kind} · ")))
                    && notice.parent_id.is_none(),
                &format!("{name}: {} main '{}'", run.id, notice.title),
            );
            // A run with Claude's answer is complete; only a stream that ends inside a turn answering in it again leaves it running.
            if let Some(answer) = &notice.output {
                check(
                    notice.state == "completed" || (ends_in_turn && notice.state == "running"),
                    &format!("{name}: {} state {}", run.id, notice.state),
                );
                check(
                    !main.output.as_deref().is_some_and(|o| o.contains(answer.as_str())),
                    &format!("{name}: {} answer in the request's result", run.id),
                );
            }
            for copy in &run.copies {
                let source = copy.source_block_id.as_ref().and_then(|s| latest.get(s));
                check(source.is_some(), &format!("{name}: {} source", copy.id));
                let source = source.expect("a source");
                check(
                    copy.id == format!("{}:{}", run.id, source.id)
                        && copy.parent_id.as_deref() == Some(notice.id.as_str())
                        && copy.background == Some(true),
                    &format!("{name}: {}", copy.id),
                );
            }
        }
        // A block's latest copy is the block as it ended: kind, title, request, state, result and output file.
        let mut sources: Vec<String> = vec![];
        for source in runs.iter().flat_map(|r| r.copies.iter().filter_map(|c| c.source_block_id.clone())) {
            if !sources.contains(&source) {
                sources.push(source);
            }
        }
        for source in sources {
            let run = runs.iter().rfind(|r| r.copies.iter().any(|c| c.source_block_id.as_deref() == Some(&source))).expect("a run");
            let copy = run.copies.iter().find(|c| c.source_block_id.as_deref() == Some(&source)).expect("a copy");
            let original = &latest[&source];
            check(
                copy.kind == original.kind && copy.title == original.title && copy.input == original.input,
                &format!("{name}: {} copy", copy.id),
            );
            check(
                copy.state == original.state && copy.output == original.output && copy.output_file == original.output_file,
                &format!("{name}: {} copy", copy.id),
            );
        }
    }
}

/// The notice run title rule: kind and names in, "<kind> · <name>", "<kind> · <first> 외 <n>개" or the kind alone out.
fn notice_titles_follow_the_fixture() {
    let ko = Locale::new("ko");
    let cases = fixture_cases("noticeTitles");
    check(cases.len() >= 4, "the fixture has notice title cases");
    for item in cases {
        let names = strings(item.get("names"));
        let title = notice::title(&ko.get(text(&item, "kind").unwrap_or("")), &names, &ko);
        same(Some(title.as_str()), text(&item, "expect"), &format!("notice title: {}", text(&item, "name").unwrap_or("")));
    }
}

/// The result title rule (`MightyGraphBlockModel.ResultTitle`, MightyGraphViewModel.cs:295; the tests' copy until the view model is here).
fn result_titles_follow_the_fixture() {
    let ko = Locale::new("ko");
    let cases = fixture_cases("resultTitles");
    check(cases.len() >= 4, "the fixture has result title cases");
    for item in cases {
        let expect = &item["expect"];
        let key = text(expect, "key").unwrap_or("");
        let wanted = match expect.get("count").and_then(Value::as_i64) {
            Some(count) => ko.get_with(key, &[("count", &count.to_string())]),
            None => ko.get(key),
        };
        let waiting = item.get("waiting").and_then(Value::as_i64).unwrap_or(0) as i32;
        let awaiting = flag(&item, "awaiting");
        same(
            result_title(text(&item, "status").unwrap_or(""), waiting, awaiting, &ko),
            wanted,
            &format!("result title: {}", text(&item, "name").unwrap_or("")),
        );
    }
}

/// The awaiting-notice rule over the shared runs.
fn awaiting_notice_follows_the_fixture() {
    let ko = Locale::new("ko");
    let cases = fixture_cases("awaitingNotice");
    check(cases.len() >= 8, "the fixture has awaiting-notice cases");
    for item in cases {
        let name = text(&item, "name").unwrap_or("").to_string();
        let runs: Vec<MightyGraphRun> = items(item.get("runs")).iter().map(|r| MightyGraphRun::read(r).expect("a run")).collect();
        let awaiting = notice::awaiting_notice(&runs[0], &runs, &ko);
        let expect = &item["expect"];
        if expect.is_null() {
            check(awaiting.is_none(), &format!("awaits nothing: {name}"));
            continue;
        }
        check(awaiting.is_some(), &format!("awaits notices: {name}"));
        let awaiting = awaiting.expect("awaiting");
        same(awaiting.delivered() as i64, expect["delivered"].as_i64().unwrap_or(-1), &format!("delivered: {name}"));
        same_list(awaiting.tasks.iter().map(|t| t.status.clone()).collect(), strings(expect.get("statuses")), &format!("statuses: {name}"));
        // The shown run says the same, read with the pane's runs.
        let shown = support::shown_runs(&runs, &ko);
        check(notice::awaiting_notice(&shown[0], &[], &ko) == Some(awaiting), &format!("the shown run carries it: {name}"));
    }
}

/// A turn that would answer in an earlier notice run but brings new work opens a run for that work; the earlier run ends as it was before
/// the turn, an error included.
fn a_mixed_turn_leaves_the_earlier_run_in_its_own_state() {
    let mixed = fixture_cases("streams")
        .into_iter()
        .find(|c| text(c, "name").is_some_and(|n| n.starts_with("notice runs: a turn that also brings a new block")))
        .expect("the mixed case");
    let mut events = items(mixed.get("events"));
    // The first notice turn fails; stop after the mixed turn's answer.
    let first = events.iter().position(|e| text(e, "type") == Some("result") && text(e, "result") == Some("The build passed."));
    let mixed_end = events.iter().position(|e| text(e, "type") == Some("result") && text(e, "result") == Some("The checks passed."));
    check(matches!((first, mixed_end), (Some(f), Some(m)) if m > f), "the mixed case has both answers");
    let (first, mixed_end) = (first.expect("first"), mixed_end.expect("mixed"));
    events[first]["is_error"] = Value::Bool(true);
    let earlier = node::main_node_id("mixed:notice:1");
    let state = std::sync::Arc::new(std::sync::Mutex::new((false, false, Vec::<ExecutionGraphNode>::new())));
    let sink = state.clone();
    let watched = earlier.clone();
    let mut tracker = ExecutionGraphTracker::new(
        "mixed",
        Some("Do the request"),
        "claude",
        None,
        Box::new(move |n: &ExecutionGraphNode| {
            let mut seen = sink.lock().expect("state");
            seen.2.push(n.clone());
            if seen.0 && n.id == watched && n.state == "running" {
                seen.1 = true;
            }
        }),
        None,
        None,
        Locale::new("ko"),
    );
    for (index, event) in events.iter().enumerate().take(mixed_end + 1) {
        tracker.consume(event);
        if index == first {
            state.lock().expect("state").0 = true;
        }
    }
    let seen = state.lock().expect("state");
    let latest = latest_by_id(&seen.2);
    let runs = notice_runs(&latest);
    same(runs.len(), 2, "a mixed turn opens a second run");
    check(seen.1, "the turn about the copied build first answers in its run");
    let earlier_main = runs[0].main.clone().expect("a main");
    check(
        earlier_main.state == "error" && earlier_main.output.as_deref() == Some("The build passed."),
        &format!("the earlier run ends as it was: {} / {:?}", earlier_main.state, earlier_main.output),
    );
    let new_main = runs[1].main.clone().expect("a main");
    check(
        new_main.state == "completed" && new_main.output.as_deref() == Some("The checks passed."),
        "the new run has the mixed turn's answer",
    );
}
