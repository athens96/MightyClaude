//! The Mighty structure view against C# `MightyStructureVerification` (native/windows/MightyCode.Core.Tests/MightyStructureVerification.cs,
//! registered in Verification.cs:341-356): each request's phase, the phase columns, a free pane's rows, the cards' texts and caps, the
//! sub-agent clusters, folding, panning and following, each column on its own.

mod common;

use std::collections::HashSet;
use std::sync::Arc;

use common::*;
use mighty_core::graph::{GraphTokenUsage, MightyGraphAgent, MightyGraphRun, node_id};
use mighty_core::json::Wire;
use mighty_core::locale::{Locale, catalogue};
use mighty_core::model::{AppSnapshot, PlanRecord, RunSession, Workspace};
use mighty_graph::node::response_record;
use mighty_graph::structure::follow::{ColumnWatch, Page, PageWatch, Scroll};
use mighty_graph::structure::pan::{self, Target};
use mighty_graph::structure::wheel::Axis;
use mighty_graph::structure::{self, Excerpt, Inputs, ItemCache, Shape, Signature, Step, Tally, fold, mobile, word};
use mighty_graph::{layout, support, timeline};
use unicode_segmentation::UnicodeSegmentation;

fn cards(shape: &Shape) -> String {
    shape
        .columns
        .as_ref()
        .unwrap()
        .iter()
        .map(|c| c.items.iter().map(|i| i.run_id.as_str()).collect::<Vec<_>>().join(","))
        .collect::<Vec<_>>()
        .join("|")
}

fn steps(shape: &Shape) -> String {
    shape.columns.as_ref().unwrap().iter().map(|c| word(c.step)).collect::<Vec<_>>().join(",")
}

fn bytes(text: Option<&str>) -> usize {
    text.map_or(usize::MAX, str::len)
}

/// `MightyStructure.Make` with the C# test's arguments.
fn make(
    runs: &[MightyGraphRun],
    style: Option<&ManifestStyle>,
    current: Option<&str>,
    starting_new: bool,
    history: &[PlanRecord],
    pending: Option<&str>,
) -> Shape {
    let inputs = Inputs {
        style: style.map(|s| s as &dyn structure::StructureStyle),
        current_phase: current.and_then(|id| style.map(|s| phase(s, id))),
        starting_new,
        plan_history: Some(history),
        pending_plan_run_id: pending,
    };
    structure::make(runs, &inputs, None, &ko())
}

fn free(runs: &[MightyGraphRun]) -> Shape {
    make(runs, None, None, false, &[], None)
}

fn item(run: &MightyGraphRun, index: usize) -> structure::Item {
    structure::item_for(run, index, None, None, &ko())
}

fn clusters(run: &MightyGraphRun) -> Vec<structure::Cluster> {
    structure::clusters(run, &ko())
}

fn sig(runs: &[MightyGraphRun], style: Option<&ManifestStyle>, current: Option<&str>) -> Signature {
    let inputs = Inputs {
        style: style.map(|s| s as &dyn structure::StructureStyle),
        current_phase: current.and_then(|id| style.map(|s| phase(s, id))),
        ..Inputs::default()
    };
    Signature::new(runs, &inputs, &ko())
}

fn phase_ids(runs: &[MightyGraphRun], style: &ManifestStyle, current: Option<&str>, history: &[PlanRecord]) -> Vec<String> {
    structure::phase_ids(runs, style, current.map(|id| phase(style, id)), Some(history), None)
}

fn sourced(id: &str, input: Option<&str>, status: &str, source: &str) -> MightyGraphRun {
    run(id, input, status, vec![], None, Some(source))
}

#[test]
fn phase_assignment() {
    let flow = style("ouroboros");
    let named = [
        asked("r1", "그냥 시작해줘"),
        asked("r2", "ooo interview 할 일 앱"),
        asked("r3", "좀 더 물어봐"),
        asked("r4", "ooo status"),
        asked("r5", "ooo seed"),
        asked("r6", "ooo pm 다시"),
    ];
    let ids = phase_ids(&named, &flow, None, &[]);
    assert_eq!(
        ids,
        ["goal", "interview", "interview", "interview", "seed", "interview"],
        "runs take the recognised phase, else the previous, else the default"
    );
    assert!(
        structure::recognised_phase("ooo status", &flow).is_none()
            && structure::recognised_phase("/ouroboros:evaluate", &flow).as_deref() == Some("evaluate"),
        "a phase-less action names no phase; a slash action names its own"
    );

    let plan = style("claude-plan");
    let runs = [
        sourced("r1", Some("로그인 화면 계획 세워줘"), "completed", "s1"),
        sourced("r2", Some("테스트 돌려줘"), "completed", "s2"),
        sourced("r3", Some("이제 다음 화면"), "completed", "s3"),
        sourced("r4", Some("[계획] 새 기능"), "completed", "s4"),
    ];
    // Sent back, then approved: the latest answer counts.
    let history = [record("p1", "s1", "approvedAuto"), record("p3a", "s3", "revised"), record("p3b", "s3", "approvedConfirm")];
    assert_eq!(
        phase_ids(&runs, &plan, None, &history),
        ["execute", "plan", "execute", "plan"],
        "approved plans execute; a request with no plan answer is planning"
    );
    assert_eq!(
        phase_ids(&[done("b1")], &plan, None, &[record("p", "b1", "approvedConfirm")]),
        ["execute"],
        "a record is matched by the block id too"
    );
    assert_eq!(
        phase_ids(&runs, &plan, Some("approve"), &history),
        ["execute", "plan", "execute", "plan"],
        "the pane's phase never pulls a run into a stage its own records do not say"
    );

    // Approved, then a new request without a plan: the pane's latest request has no plan answer, so it is planning.
    let later = make(
        &[sourced("r1", None, "completed", "s1"), sourced("r2", None, "running", "s2")],
        Some(&plan),
        Some("plan"),
        false,
        &[record("p1", "s1", "approvedAuto")],
        None,
    );
    assert!(
        cards(&later) == "r2||r1" && steps(&later) == "current,pending,done",
        "claude-plan: approved, then a new request: {} {}",
        cards(&later),
        steps(&later)
    );
    let executing =
        make(&[sourced("r1", None, "running", "s1")], Some(&plan), Some("execute"), false, &[record("p1", "s1", "approvedAuto")], None);
    assert!(
        cards(&executing) == "||r1" && steps(&executing) == "done,done,current",
        "claude-plan: executing after approval: {} {}",
        cards(&executing),
        steps(&executing)
    );
    // The revised plan's run asked again; that plan waits for an answer.
    let again = [sourced("r0", None, "completed", "s0"), sourced("r1", None, "running", "s1")];
    let answered = [record("p0", "s0", "approvedAuto"), record("p1", "s1", "revised")];
    let waiting = make(&again, Some(&plan), Some("approve"), false, &answered, Some("s1"));
    assert!(
        cards(&waiting) == "|r1|r0" && steps(&waiting) == "done,current,done",
        "claude-plan: sent back, then waiting again: {} {}",
        cards(&waiting),
        steps(&waiting)
    );
    // A pending plan whose run has drawn no block yet moves no card: the waiting column is current and empty until it does.
    let early = make(&again[..1], Some(&plan), Some("approve"), false, &answered, Some("s-new"));
    assert!(
        cards(&early) == "||r0" && steps(&early) == "done,current,done",
        "claude-plan: a pending plan with no block yet: {} {}",
        cards(&early),
        steps(&early)
    );

    let fresh =
        make(&[sourced("r1", None, "completed", "s1")], Some(&plan), Some("plan"), true, &[record("p1", "s1", "approvedAuto")], None);
    assert!(
        cards(&fresh) == "||r1" && steps(&fresh) == "current,pending,done",
        "starting anew leaves a finished card in its done column: {} {}",
        cards(&fresh),
        steps(&fresh)
    );
    // A recognised-action style: the start phase is current, the requests stay where they were.
    let restart = make(&[asked("r1", "ooo interview 앱"), asked("r2", "ooo seed")], Some(&flow), Some("goal"), true, &[], None);
    assert!(
        cards(&restart) == "|r1|r2|||" && steps(&restart) == "current,done,done,pending,pending,pending",
        "starting anew in a recognised-action style: {} {}",
        cards(&restart),
        steps(&restart)
    );
    assert_eq!(
        phase_ids(
            &[sourced("r1", Some("ooo seed"), "completed", "s1"), sourced("r2", None, "completed", "s2")],
            &flow,
            None,
            &[record("p1", "s1", "approvedAuto")]
        ),
        ["seed", "seed"],
        "plan records only matter to a plan-state style"
    );
}

#[test]
fn columns_and_free_rows() {
    let flow = style("ouroboros");
    let newest = make(
        &[asked("r1", "ooo interview 앱"), asked("r2", "더 물어봐"), asked("r3", "ooo seed")],
        Some(&flow),
        Some("run"),
        false,
        &[],
        None,
    );
    let ids: Vec<&str> = newest.columns.as_ref().unwrap().iter().map(|c| c.id()).collect();
    assert_eq!(ids, ["goal", "interview", "seed", "run", "evaluate", "evolve"], "one column per phase, in order");
    // The pane stands in run: the newest card goes there, so the bar and the cards agree.
    assert!(
        cards(&newest) == "|r1,r2||r3||" && steps(&newest) == "done,done,done,current,pending,pending",
        "the newest card sits in the pane's phase: {} {}",
        cards(&newest),
        steps(&newest)
    );
    assert_eq!(
        newest.columns.as_ref().unwrap()[1].items.iter().map(|i| i.ordinal).collect::<Vec<_>>(),
        [1, 2],
        "oldest first under their phase"
    );
    // The flow went back to the interview after a seed.
    let back = make(&[asked("r1", "ooo seed"), asked("r2", "ooo interview 다시")], Some(&flow), Some("interview"), false, &[], None);
    assert!(
        cards(&back) == "|r2|r1|||" && steps(&back) == "done,current,done,pending,pending,pending",
        "a column holding cards is never pending: {} {}",
        cards(&back),
        steps(&back)
    );
    // Four phases: sketch, outline, build, wrap.
    let looped = fixture_style("loop");
    assert_eq!(
        steps(&make(
            &[asked("r1", "/loop:sketch-idea 아이디어"), asked("r2", "/loop:write-outline")],
            Some(&looped),
            None,
            false,
            &[],
            None
        )),
        "done,current,pending,pending",
        "with no current phase the newest request's phase is current"
    );

    let runs = [
        status("running", "running"),
        run("done", None, "completed", vec![], Some("끝났어요\n두 번째 줄"), None),
        status("failed", "error"),
        status("halted", "stopped"),
        // The request settled but a block still runs: the run is not over yet.
        run("tail", None, "completed", vec![agent("bg", None, "running", Some("task"))], None, None),
    ];
    // A style without phases is drawn as a free pane, as no style at all is.
    let deck = fixture_style("deck");
    assert!(make(&runs, Some(&deck), None, false, &[], None) == free(&runs), "a style without phases draws a free pane");
    let shape = free(&runs);
    let rows = shape.rows.as_ref().unwrap();
    assert!(
        rows.iter().map(|r| r.item.run_id.as_str()).collect::<Vec<_>>() == ["running", "done", "failed", "halted", "tail"]
            && rows.iter().all(|r| r.request == Step::Done),
        "one row per request, oldest first, each with its request done"
    );
    assert_eq!(
        rows.iter().map(|r| r.execution).collect::<Vec<_>>(),
        [Step::Current, Step::Done, Step::Error, Step::Stopped, Step::Current],
        "the run goes on until every block settled, unless the request failed or was stopped"
    );
    assert_eq!(
        rows.iter().map(|r| r.result).collect::<Vec<_>>(),
        [Step::Pending, Step::Done, Step::Error, Step::Stopped, Step::Pending],
        "the result waits until the request finished"
    );
    assert_eq!(
        rows.iter().map(|r| r.item.status.as_str()).collect::<Vec<_>>(),
        ["running", "completed", "error", "stopped", "completed"],
        "statuses are bucketed as a block's are"
    );
    assert!(
        rows[1].item.result.as_deref() == Some("끝났어요 두 번째 줄") && rows[0].item.result.is_none() && rows[1].item.input == "do done",
        "the result on one line, and only once the request finished"
    );
    assert!(
        rows[1].item.node_id == node_id("done", "request")
            && rows[1].item.execution_node_id == node_id("done", structure::EXECUTION_SUFFIX),
        "a card names its request's and its run's diagram nodes"
    );

    assert!(free(&[]) == Shape::free(vec![]), "an empty pane has no requests");
    let idle = make(&[], Some(&flow), None, false, &[], None);
    let idle = idle.columns.unwrap();
    assert!(
        idle.len() == 6 && idle.iter().all(|c| c.items.is_empty() && c.step == Step::Pending),
        "a style's columns show before the first request, all pending"
    );
    assert_eq!(
        steps(&make(&[], Some(&flow), Some("goal"), false, &[], None)),
        "current,pending,pending,pending,pending,pending",
        "the pane's phase is current before the first request"
    );
}

#[test]
fn cards_show_the_work_in_place() {
    let mut work = status("r", "running");
    work.input = Some("화면을 고쳐줘".into());
    work.root_entries = Some(vec![said("m1", "읽어 볼게요"), read("t1", "a.swift")]);
    let card = item(&work, 0);
    assert!(
        card.request.as_ref().map(|r| r.text.as_str()) == Some("화면을 고쳐줘") && card.input == "화면을 고쳐줘",
        "the request part is the input alone"
    );
    assert!(
        card.message.as_ref().map(|m| m.text.as_str()) == Some("읽어 볼게요")
            && card.activity.iter().map(|a| a.id.as_str()).collect::<Vec<_>>() == ["t1"]
            && card.status == "running",
        "the run part reads the main block's records"
    );
    assert!(
        card.execution_node_id == node_id("r", "execution") && card.execution_node_id != card.node_id,
        "the run part stands for its own diagram node"
    );
    let branched_run = run("r", Some("화면을 고쳐줘"), "running", vec![sub("a")], None, None);
    let branched = item(&branched_run, 0);
    let laid = layout::make(std::slice::from_ref(&branched_run), "", true, &HashSet::new(), &layout::Options::default(), &ko());
    assert!(
        laid.nodes.iter().any(|n| n.id == branched.node_id) && laid.nodes.iter().any(|n| n.id == branched.clusters[0].nodes[0].node_id),
        "the request and its sub-agent are nodes the diagram lays out"
    );
    let fresh = item(&status("n", "running"), 0);
    assert!(
        fresh.message.is_none() && fresh.activity.is_empty() && !fresh.finished,
        "a request with nothing yet still has its run part, waiting"
    );

    let mut counted = run(
        "r",
        None,
        "completed",
        vec![sub("a"), agent("t", None, "completed", Some("task")), agent("s", None, "completed", Some("steer"))],
        None,
        None,
    );
    let turn = mighty_core::model::LogEntry {
        activity: Some(activity("y", "turn", "completed", "turn", None)),
        ..entry("y", "system", "turn", STAMP)
    };
    counted.root_entries = Some(vec![read("x", "README.md"), turn]);
    let agents = counted.agents.as_mut().unwrap();
    agents[0].entries = Some(vec![tool("z", "a.swift", "edit", "running")]);
    // A steer's records are not a sub-agent's work.
    agents[2].entries = Some(vec![read("w", "b.swift")]);
    assert_eq!(item(&counted, 0).tally, Tally { agents: 1, tools: 2 }, "the tally counts sub-agents and tool calls");

    let mut live = run("r", Some("로그인 화면을 고쳐줘\n두 번째 줄"), "running", vec![], None, None);
    let turn = mighty_core::model::LogEntry {
        activity: Some(activity("turn", "turn", "running", "turn", None)),
        ..entry("turn", "system", "turn", STAMP)
    };
    live.root_entries = Some(vec![
        said("m1", "먼저 읽어 볼게요"),
        read("t1", "a.swift"),
        turn,
        tool("t2", "login", "search", "completed"),
        tool("t3", "b.swift", "edit", "completed"),
        tool("t4", "swift test\n--filter Login", "command", "running"),
        said("m2", "고치는 중이에요\n테스트를 돌립니다"),
        tool("t5", "", "web", "completed"),
        read("t6", "c.swift"),
        // A question's raw form is not a reply.
        said(
            "q",
            "{\"questions\":[{\"header\":\"H\",\"question\":\"Q?\",\"multiSelect\":false,\"options\":[{\"label\":\"A\",\"description\":\"\"},{\"label\":\"B\",\"description\":\"\"}]}]}",
        ),
    ]);
    let running = item(&live, 0);
    let message = running.message.clone().expect("a message");
    assert!(
        running.request.as_ref().map(|r| r.text.as_str()) == Some("로그인 화면을 고쳐줘\n두 번째 줄")
            && message.text == "고치는 중이에요\n테스트를 돌립니다"
            && !message.cut()
            && !message.folds(structure::MESSAGE_LINES, 20),
        "a running request shows its latest message"
    );
    // The last five tool calls, oldest first, on one line each; the turn row and a call with no words (nor a tool name) are not shown.
    assert!(
        running.activity.iter().map(|a| a.id.as_str()).collect::<Vec<_>>() == ["t1", "t2", "t3", "t4", "t6"]
            && running.activity.iter().map(|a| a.kind.as_str()).collect::<Vec<_>>() == ["read", "search", "edit", "command", "read"],
        "a running request shows its last five tool calls"
    );
    assert!(
        running.activity[3].text == "swift test --filter Login"
            && running.activity[3].state == "running"
            && running.answer.is_none()
            && running.result.is_none(),
        "a tool call is its summary on one line; a running request has no answer"
    );

    let answer = "# 끝났어요\n\n- 로그인 수정\n- 테스트 통과";
    let finished_run = run("r", None, "completed", vec![], Some(answer), None);
    let finished = item(&finished_run, 0);
    // RefreshResult put the answer in the main block too.
    assert!(
        finished_run.root_entries().last().map(|e| e.text()) == Some(answer)
            && finished.answer.as_ref().is_some_and(|a| a.text == answer && a.preview == answer)
            && finished.message.is_none(),
        "a finished request shows its result once"
    );
    let mut failed = status("f", "error");
    failed.root_entries = Some(vec![said("m", "권한이 없어요")]);
    let failed = item(&failed, 0);
    assert!(
        failed.answer.is_none() && failed.message.as_ref().map(|m| m.text.as_str()) == Some("권한이 없어요"),
        "a failed request has no answer; its last reply stays its message"
    );

    let reviewer = MightyGraphAgent {
        title: Some("검토".into()),
        input: Some("변경을 검토해 줘\n특히 보안".into()),
        response_records: Some(vec![
            Some(response_record("x", None, GraphTokenUsage::default(), &[], false)),
            Some(response_record("y", Some("claude-opus-4-1"), GraphTokenUsage::default(), &[], false)),
        ]),
        entries: Some(vec![
            read("a1", "a.swift"),
            said("o1", "문제 없음"),
            read("a2", "b.swift"),
            read("a3", "c.swift"),
            tool("a4", "make", "command", "completed"),
        ]),
        ..block("a")
    };
    let build = MightyGraphAgent {
        parent_id: Some("a".into()),
        title: Some("빌드".into()),
        input: Some("make all".into()),
        status: Some("completed".into()),
        kind: Some("task".into()),
        entries: Some(vec![said("t-answer", "빌드 성공")]),
        ..block("t")
    };
    let nodes: Vec<_> = clusters(&run("r", None, "running", vec![reviewer, build], None, None)).into_iter().flat_map(|c| c.nodes).collect();
    assert!(
        nodes.iter().map(|n| n.agent_id.as_str()).collect::<Vec<_>>() == ["a", "t"]
            && nodes[0].prompt.as_ref().is_some_and(|p| p.text == "변경을 검토해 줘\n특히 보안" && !p.folds(structure::PROMPT_LINES, 20)),
        "a sub-agent carries its prompt"
    );
    assert!(
        nodes[0].output.as_ref().map(|o| o.text.as_str()) == Some("문제 없음")
            && nodes[0].activity.iter().map(|a| a.id.as_str()).collect::<Vec<_>>() == ["a2", "a3", "a4"]
            && nodes[0].model.as_deref() == Some("claude-opus-4-1"),
        "a sub-agent carries its output, its last three tool calls and its model"
    );
    assert!(
        nodes[1].prompt.as_ref().map(|p| p.text.as_str()) == Some("make all")
            && nodes[1].output.as_ref().map(|o| o.text.as_str()) == Some("빌드 성공")
            && nodes[1].activity.is_empty()
            && nodes[1].model.is_none(),
        "a task carries its command and its answer"
    );
}

#[test]
fn texts_are_capped_and_folded() {
    let syllables = "가".repeat(3_000); // 9,000 bytes
    let request = structure::excerpt_of(&syllables, structure::REQUEST_BYTES, structure::REQUEST_LINES, false).expect("an excerpt");
    assert!(
        request.text.len() <= structure::REQUEST_BYTES && request.text.ends_with('…') && request.cut(),
        "a long request is cut at its cap and marked"
    );
    // A long line is cut in the preview, so a folded card never lays out the whole text.
    assert!(request.preview.graphemes(true).count() <= structure::PREVIEW_LINE_CHARACTERS, "a long line is cut in the preview");
    let twenty = (1..=20).map(|i| format!("line {i}")).collect::<Vec<_>>().join("\n");
    let folded = structure::excerpt_of(&twenty, structure::MESSAGE_BYTES, structure::MESSAGE_LINES, false).unwrap();
    assert!(
        folded.cut() && folded.text == twenty && folded.preview == (1..=8).map(|i| format!("line {i}")).collect::<Vec<_>>().join("\n"),
        "the first lines fold, the text keeps them all"
    );
    assert!(
        structure::excerpt_of("짧은 글", 100, 2, false) == Some(Excerpt { text: "짧은 글".into(), preview: "짧은 글".into() })
            && structure::excerpt_of(" \n\u{1b}[0m ", 100, 2, false).is_none(),
        "short text fits as it is; nothing but blanks or controls is nothing"
    );
    // The Windows composer ends a typed line with a bare CR, a pasted one with CR LF: both are lines, on the desktop and on the phone.
    let typed = (1..=12).map(|i| format!("line {i}")).collect::<Vec<_>>().join("\r");
    let read_back = typed.replace('\r', "\n");
    let asked_excerpt = structure::excerpt_of(&typed, structure::REQUEST_BYTES, structure::REQUEST_LINES, false).unwrap();
    assert!(
        asked_excerpt.cut()
            && asked_excerpt.text == read_back
            && asked_excerpt.preview == (1..=10).map(|i| format!("line {i}")).collect::<Vec<_>>().join("\n"),
        "a request typed with bare carriage returns folds at its ten lines"
    );
    assert!(
        structure::excerpt_of("a\r\nb\rc\nd", 100, 3, false) == Some(Excerpt { text: "a\nb\nc\nd".into(), preview: "a\nb\nc".into() }),
        "CR LF, CR and LF each end one line"
    );
    assert_eq!(
        structure::excerpt_of("```\r\ncode\r\nmore", 100, 2, true).map(|e| e.preview).as_deref(),
        Some("```\ncode\n```"),
        "a fence is found and closed in a text with carriage returns"
    );
    assert_eq!(
        mobile::item_for(&item(&asked("typed", &typed), 0), None, None, None).request.as_deref(),
        Some(read_back.as_str()),
        "the phone is sent the same lines"
    );
    // The result and the sub-agent texts take their own caps.
    let big = "result ".repeat(5_000);
    let capped = item(&run("r", Some(&syllables), "completed", vec![], Some(&big), None), 0);
    assert!(
        bytes(capped.request.as_ref().map(|r| r.text.as_str())) <= structure::REQUEST_BYTES
            && capped.answer.as_ref().is_some_and(|a| a.cut() && a.text.len() <= structure::ANSWER_BYTES && a.text.ends_with('…')),
        "the request and the answer take their own caps"
    );
    let heavy = MightyGraphAgent {
        input: Some(syllables.clone()),
        status: Some("completed".into()),
        entries: Some(vec![said("o", &big)]),
        ..block("a")
    };
    let node = clusters(&run("r2", None, "completed", vec![heavy], None, None))[0].nodes[0].clone();
    assert!(
        bytes(node.prompt.as_ref().map(|p| p.text.as_str())) <= structure::PROMPT_BYTES
            && bytes(node.output.as_ref().map(|o| o.text.as_str())) <= structure::OUTPUT_BYTES,
        "a sub-agent's prompt and output take their own caps"
    );

    // A node six levels deep in the narrowest column has 196pt for its text.
    let width = structure::node_text_width(structure::MINIMUM_COLUMN_WIDTH, 6);
    assert!(
        width == 196.0 && structure::node_text_width(structure::MINIMUM_COLUMN_WIDTH, 60) == width,
        "a deep node's text width stops shrinking at the indent cap: {width}"
    );
    // About 17 Hangul syllables fit a 196pt line at 11pt; words wrap whole, so fewer are counted.
    let per_line = structure::characters_per_line(width, 11.0);
    assert!(per_line <= 17, "a wrapped line is counted as only partly filled: {per_line}");
    // 120 syllables on one line take 8 lines of 17: past the 6 an output shows folded.
    let output = structure::excerpt_of(&"가나다라마 ".repeat(20), structure::OUTPUT_BYTES, structure::OUTPUT_LINES, false).unwrap();
    assert!(!output.cut() && output.folds(structure::OUTPUT_LINES, per_line), "a text that wraps past its lines folds without being cut");
    // Any text that would wrap past its lines at the real width folds.
    for count in (1..=200usize).step_by(7) {
        assert!(
            count.div_ceil(17) <= 6 || structure::excerpt_of(&"가".repeat(count), 4_096, 6, false).is_some_and(|e| e.folds(6, per_line)),
            "{count} syllables must fold"
        );
    }
    // The prompt's two lines at its smaller size, the same way.
    assert!(
        structure::excerpt_of(&"검토".repeat(20), structure::PROMPT_BYTES, structure::PROMPT_LINES, false)
            .is_some_and(|e| e.folds(structure::PROMPT_LINES, structure::characters_per_line(width, 10.5))),
        "a prompt folds at its own size"
    );

    let mut lines: Vec<String> = vec!["# 결과".into(), String::new(), "```swift".into()];
    lines.extend((1..=20).map(|i| format!("let value{i} = {i}")));
    lines.extend(["```".to_string(), "끝".to_string()]);
    let fenced = structure::excerpt_of(&lines.join("\n"), structure::ANSWER_BYTES, structure::ANSWER_LINES, true).unwrap();
    // The first fifteen lines whole, and the fence they leave open closed.
    let mut expected: Vec<&str> = lines.iter().take(15).map(String::as_str).collect();
    expected.push("```");
    assert!(
        fenced.cut() && fenced.preview == expected.join("\n") && structure::open_fence(&fenced.preview).is_none(),
        "a folded answer keeps whole lines and closes its code fence"
    );
    assert!(
        structure::open_fence("~~~~\ncode").as_deref() == Some("~~~~") && structure::open_fence("```\ncode\n```").is_none(),
        "a tilde fence is closed with its own mark; a closed one is left alone"
    );
    // Lines stop at the preview's budget rather than being cut mid-line.
    let wide_text = (1..=15).map(|i| format!("{i} {}", "x".repeat(300))).collect::<Vec<_>>().join("\n");
    let wide = structure::excerpt_of(&wide_text, structure::ANSWER_BYTES, structure::ANSWER_LINES, true).unwrap();
    assert!(
        wide.preview.split('\n').all(|l| l.len() == 302 || l.len() == 303)
            && wide.preview.len() <= structure::ANSWER_LINES * structure::PREVIEW_LINE_CHARACTERS,
        "a Markdown preview stops at whole lines within its budget"
    );
    assert!(structure::excerpt_of("**됐어요**", 100, 15, true).is_some_and(|e| !e.cut()), "a short answer is shown whole, with no toggle");

    // Three columns in 1,000pt with 12pt gaps: (1000 - 24) / 3. Too narrow: the minimum, and the view scrolls sideways.
    assert!(
        structure::column_width(1_000.0, 3, 12.0) == 325.0
            && structure::column_width(700.0, 6, 12.0) == structure::MINIMUM_COLUMN_WIDTH
            && structure::column_width(2_000.0, 0, 12.0) == structure::MINIMUM_COLUMN_WIDTH,
        "columns share the pane's width down to the minimum"
    );
}

fn joined(clusters: &[structure::Cluster]) -> String {
    clusters.iter().map(|c| c.nodes.iter().map(|n| n.agent_id.as_str()).collect::<Vec<_>>().join(",")).collect::<Vec<_>>().join("|")
}

#[test]
fn clusters_are_the_root_trees() {
    let agents = vec![
        sub("a"),
        agent("b", Some("a"), "running", None),
        agent("c", Some("b"), "completed", None),
        agent("s", None, "completed", Some("steer")),
        agent("d", Some("ghost"), "completed", None), // an unknown parent is a root
        agent("q", Some("a"), "completed", Some("question")),
        agent("t", Some("a"), "completed", Some("task")),
        agent("e", Some("e"), "completed", None), // its own parent is a root
        agent("k", None, "completed", Some("compact")),
        agent("u", Some("k"), "completed", None), // under a left-out block: a root
    ];
    let found = clusters(&run("r", None, "completed", agents, None, None));
    assert_eq!(joined(&found), "a,b,c,t|d|e|u", "one cluster per root tree, flattened depth-first");
    assert!(
        found[0].nodes.iter().map(|n| n.depth).collect::<Vec<_>>() == [0, 1, 2, 1]
            && found[0].nodes.iter().map(|n| n.kind.as_str()).collect::<Vec<_>>() == ["agent", "agent", "agent", "task"]
            && found[0].nodes[1].status == "running",
        "a node carries its depth, kind and status"
    );
    assert!(
        found[0].nodes[0].node_id == node_id("r", "agent:a")
            && found[0].nodes[0].title == "agent a"
            && found[0].id() == found[0].nodes[0].node_id,
        "a node names its diagram block; a cluster is named by its root"
    );

    let repeated = || vec![sub("a"), agent("b", Some("a"), "completed", None), agent("a", Some("b"), "completed", None), sub("a")];
    let kept = clusters(&run("r", None, "completed", repeated(), None, None));
    assert!(
        kept.len() == 1
            && kept[0].nodes.iter().map(|n| n.agent_index).collect::<Vec<_>>() == [0, 1]
            && item(&run("r", None, "completed", repeated(), None, None), 0).tally.agents == 2,
        "a repeated block id is kept at its first place"
    );

    let cycle = clusters(&run(
        "r",
        None,
        "completed",
        vec![agent("x", Some("y"), "completed", None), agent("y", Some("x"), "completed", None)],
        None,
        None,
    ));
    assert!(
        cycle.len() == 1 && joined(&cycle) == "x,y" && cycle[0].nodes.iter().map(|n| n.depth).collect::<Vec<_>>() == [0, 1],
        "a cycle shows each block once"
    );

    let parents: Vec<String> = (0..70).map(|i| format!("n{i}")).collect();
    let chain_agents = (0..70).map(|i| agent(&parents[i], if i == 0 { None } else { Some(&parents[i - 1]) }, "completed", None)).collect();
    let chain = clusters(&run("r", None, "completed", chain_agents, None, None));
    assert!(
        chain.len() == 2
            && chain[0].nodes.len() == structure::MAXIMUM_DEPTH
            && chain[0].nodes.last().unwrap().depth == structure::MAXIMUM_DEPTH - 1,
        "a deep chain stops at the cap"
    );
    assert!(
        chain[1].nodes.iter().map(|n| n.agent_id.clone()).collect::<Vec<_>>() == (64..70).map(|i| format!("n{i}")).collect::<Vec<_>>()
            && chain[1].nodes[0].depth == 0
            && chain.iter().map(|c| c.nodes.len()).sum::<usize>() == 70,
        "what the cap left out goes on in a new cluster"
    );
}

#[test]
fn a_task_notice_is_a_card_after_its_task() {
    let notice = MightyGraphAgent {
        kind: Some("steer".into()),
        status: Some("completed".into()),
        input: Some(format!("{}Build · 완료\nBuild done", support::TASK_NOTICE_PREFIX)),
        entries: Some(vec![said("n-answer", "The build passed.")]),
        ..block("n")
    };
    let notice_run = run(
        "r",
        None,
        "completed",
        vec![agent("t", None, "completed", Some("task")), agent("s", None, "completed", Some("steer")), notice],
        None,
        None,
    );
    let found = clusters(&notice_run);
    assert_eq!(joined(&found), "t|n", "the notice is its own card after its task, a person's steer is left out");
    let card = &found[1].nodes[0];
    assert!(
        card.title == "작업 알림"
            && card.kind == "steer"
            && card.prompt.as_ref().map(|p| p.text.as_str()) == Some("Build · 완료\nBuild done")
            && card.output.as_ref().map(|o| o.text.as_str()) == Some("The build passed."),
        "the card is titled 작업 알림 and shows the notice line and the reply; got {:?} / {:?}",
        card.prompt,
        card.output
    );
    assert_eq!(item(&notice_run, 0).tally.agents, 0, "a notice is not counted as a sub-agent");
}

#[test]
fn signature_and_item_cache() {
    let flow = style("ouroboros");
    let live = |agent_status: &str| run("r", None, "running", vec![agent("a", None, agent_status, None)], None, None);
    let mut first = live("running");
    let before = sig(std::slice::from_ref(&first), Some(&flow), Some("goal"));
    assert!(before == sig(std::slice::from_ref(&first), Some(&flow), Some("goal")), "the signature of the same pane is the same");
    // A streamed line may add a tool call.
    first.agents.as_mut().unwrap()[0].entries.as_mut().unwrap().push(entry("added", "system", "read", STAMP));
    assert!(
        sig(std::slice::from_ref(&first), Some(&flow), Some("goal")) != before
            && before != sig(&[live("completed")], Some(&flow), Some("goal")),
        "a new record or a block's status moves the signature"
    );
    let goal = phase(&flow, "goal");
    let anew = Signature::new(
        &[live("running")],
        &Inputs { style: Some(&flow), current_phase: Some(goal), starting_new: true, ..Inputs::default() },
        &ko(),
    );
    let pending = Signature::new(
        &[live("running")],
        &Inputs { style: Some(&flow), current_phase: Some(goal), pending_plan_run_id: Some("s"), ..Inputs::default() },
        &ko(),
    );
    assert!(before != anew && before != pending, "starting anew or a pending plan moves the signature");

    let mut grown = live("running");
    grown.root_entries = Some(vec![said("m", "읽는 중")]);
    grown.agents.as_mut().unwrap()[0].entries = Some(vec![tool("t", "a.swift", "read", "running")]);
    let still = sig(std::slice::from_ref(&grown), None, None);
    // A reply that grew by a few lines keeps its record and the count.
    let mut longer = grown.clone();
    longer.root_entries.as_mut().unwrap()[0].text = Some(format!("읽는 중{}", "\n이제 고칩니다".repeat(20)));
    assert!(sig(&[longer], None, None) != still, "a reply that grew in place moves the signature");
    // A tool call that finished, and a sub-agent's prompt that arrived.
    let mut settled = grown.clone();
    settled.agents.as_mut().unwrap()[0].entries.as_mut().unwrap()[0].activity.as_mut().unwrap().state = "completed".into();
    let mut prompted = grown.clone();
    prompted.agents.as_mut().unwrap()[0].input = Some("검토해 줘".into());
    assert!(
        sig(&[settled], None, None) != still && sig(&[prompted], None, None) != still,
        "a tool call that finished or a prompt that arrived moves the signature"
    );

    let mut stream = status("r", "running");
    let mut root = vec![tool("old", "swift build", "read", "running")];
    root.extend((1..=20).map(|i| read(&format!("t{i}"), &format!("f{i}.swift"))));
    root.push(said("m", "가"));
    stream.root_entries = Some(root);
    let quiet = sig(std::slice::from_ref(&stream), None, None);
    // A few more words in the streamed reply: nothing to rebuild yet. A few lines more: the card is built again.
    let mut words = stream.clone();
    words.root_entries.as_mut().unwrap().last_mut().unwrap().text = Some("가 나다".into());
    let mut more = stream.clone();
    more.root_entries.as_mut().unwrap().last_mut().unwrap().text = Some("한 줄의 글입니다\n".repeat(12));
    assert!(sig(&[words], None, None) == quiet && sig(&[more], None, None) != quiet, "the signature moves every few lines, not every word");
    // A tool call older than the newest records finishing in place.
    let mut older = stream.clone();
    older.root_entries.as_mut().unwrap()[0].activity.as_mut().unwrap().state = "completed".into();
    assert!(sig(&[older], None, None) != quiet, "an older tool call finishing in place moves the signature");

    let ko = ko();
    let mut runs = vec![run("r1", None, "completed", vec![], Some("첫 결과"), None), status("r2", "running")];
    let fresh = |runs: &[MightyGraphRun]| runs.iter().enumerate().map(|(i, r)| item(r, i)).collect::<Vec<_>>();
    let values = |items: &[Arc<structure::Item>]| items.iter().map(|i| (**i).clone()).collect::<Vec<_>>();
    let mut cache = ItemCache::new();
    assert!(values(&cache.items(&runs, &sig(&runs, None, None), &ko)) == fresh(&runs), "the cache builds every card the first time");
    runs[1].root_entries.as_mut().unwrap().push(said("m", "진행 중"));
    let second = cache.items(&runs, &sig(&runs, None, None), &ko);
    assert!(
        second[1].message.as_ref().map(|m| m.text.as_str()) == Some("진행 중") && values(&second) == fresh(&runs),
        "the cache rebuilds the request that moved"
    );
    assert!(Arc::ptr_eq(&second[0], &cache.items(&runs, &sig(&runs, None, None), &ko)[0]), "a card that kept its place is not copied");
    // The oldest request trimmed: a kept card takes its new number.
    let trimmed = runs[1..].to_vec();
    let third = cache.items(&trimmed, &sig(&trimmed, None, None), &ko);
    assert!(values(&third) == [item(&trimmed[0], 0)] && third[0].ordinal == 1, "a kept card takes its new number after a trim");
    assert!(
        structure::make(&trimmed, &Inputs::default(), Some(&third), &ko) == structure::make(&trimmed, &Inputs::default(), None, &ko),
        "the shape built from cached cards is the shape built from scratch"
    );

    // Views sign the shown runs, which no longer hold the background block: its ending moves only the waiting count and the awaiting.
    let shown = |task_status: &str| {
        let task = MightyGraphAgent { background: Some(true), ..agent("t", None, task_status, Some("task")) };
        support::shown(&run("r", None, "completed", vec![task], Some("Built."), None), &ko)
    };
    let waiting = shown("running");
    let ended = shown("completed");
    assert!(
        waiting.agents().is_empty() && support::background_waiting(&waiting) == 1 && support::background_waiting(&ended) == 0,
        "the shown run keeps the waiting count"
    );
    let waiting_signature = sig(std::slice::from_ref(&waiting), None, None);
    let ended_signature = sig(std::slice::from_ref(&ended), None, None);
    assert!(waiting_signature != ended_signature, "the waiting count moves the signature");
    let mut shown_cache = ItemCache::new();
    let card = shown_cache.items(std::slice::from_ref(&waiting), &waiting_signature, &ko)[0].clone();
    let after = shown_cache.items(std::slice::from_ref(&ended), &ended_signature, &ko)[0].clone();
    assert!(
        card.background_waiting == 1 && card.awaiting && after.background_waiting == 0 && !after.awaiting,
        "the card is built again when the count moves"
    );
    // A notice turn answered moves no field of the request, yet ends its awaiting: the signature, read with the pane's runs, moves.
    let mut request = run("r", None, "completed", vec![agent("t", None, "completed", Some("task"))], Some("Built."), None);
    request.agents.as_mut().unwrap()[0].background = Some(true);
    let mut notice = MightyGraphRun {
        id: Some("r:notice:1".into()),
        input: Some(String::new()),
        status: Some("running".into()),
        notice_run: Some(true),
        root_entries: Some(vec![]),
        result_entries: Some(vec![]),
        agents: Some(vec![MightyGraphAgent {
            kind: Some("task".into()),
            status: Some("completed".into()),
            background: Some(true),
            source_block_id: Some("t".into()),
            ..block("r:notice:1:t")
        }]),
        ..MightyGraphRun::default()
    };
    let pair = |notice: &MightyGraphRun| vec![request.clone(), notice.clone()];
    let answering = sig(&pair(&notice), None, None);
    assert!(shown_cache.items(&pair(&notice), &answering, &ko)[0].awaiting, "awaiting while the notice turn runs");
    notice.status = Some("completed".into());
    let answered = sig(&pair(&notice), None, None);
    assert!(
        answering != answered && !shown_cache.items(&pair(&notice), &answered, &ko)[0].awaiting,
        "the answered notice ends the card's awaiting"
    );

    // A block without a title is named in the app's language: a live language switch moves the signature and reads the cards again.
    let untitled = vec![run("u", None, "completed", vec![MightyGraphAgent { status: Some("completed".into()), ..block("a") }], None, None)];
    let en = Locale::new("en");
    let mut named = ItemCache::new();
    let korean = Signature::new(&untitled, &Inputs::default(), &ko);
    let title = named.items(&untitled, &korean, &ko)[0].clusters[0].nodes[0].title.clone();
    let english = Signature::new(&untitled, &Inputs::default(), &en);
    assert!(
        title == catalogue("ko")["graph.block.agent"]
            && english != korean
            && named.items(&untitled, &english, &en)[0].clusters[0].nodes[0].title == catalogue("en")["graph.block.agent"],
        "another language names an untitled block again"
    );
}

#[test]
fn saved_choice_survives_a_restart() {
    let pane = RunSession {
        id: Some("s".into()),
        workspace_id: Some("w".into()),
        agent_view_mode: Some("mighty".into()),
        draft: Some("초안".into()),
        ..RunSession::default()
    };
    let chosen = timeline::apply_mode(&pane, "structure");
    assert!(
        chosen.graph_view_mode.as_deref() == Some("structure")
            && timeline::mode(&chosen) == "structure"
            && chosen.draft == pane.draft
            && chosen.agent_view_mode.as_deref() == Some("mighty"),
        "the structure choice changes only the pane's graph presentation"
    );
    let json = chosen.to_json();
    let parsed: serde_json::Value = serde_json::from_str(&json).unwrap();
    assert!(
        json.contains("\"graphViewMode\":\"structure\"")
            && RunSession::read(&parsed).unwrap().graph_view_mode.as_deref() == Some("structure"),
        "the structure choice round-trips as its word"
    );
    // A build that does not know a word reads the pane as the diagram.
    let unknown: serde_json::Value = serde_json::from_str("{\"id\":\"s\",\"workspaceId\":\"w\",\"graphViewMode\":\"structure2\"}").unwrap();
    assert_eq!(timeline::mode(&RunSession::read(&unknown).unwrap()), "diagram", "an unknown word reads as the diagram");
    let directory = std::env::temp_dir().to_string_lossy().into_owned();
    let shell = RunSession { id: Some("sh".into()), kind: Some("shell".into()), ..chosen.clone() };
    let state = AppSnapshot {
        workspaces: Some(vec![Workspace { id: Some("w".into()), path: Some(directory), ..Workspace::default() }]),
        sessions: Some(vec![Arc::new(chosen), Arc::new(shell)]),
        ..AppSnapshot::default()
    };
    let restored = mighty_core::normalize::normalize(&state, true).unwrap();
    let mode_of = |id: &str| restored.sessions().iter().find(|s| s.id() == id).and_then(|s| s.graph_view_mode.clone());
    assert!(
        mode_of("s").as_deref() == Some("structure") && mode_of("sh").is_none(),
        "a restart keeps the structure choice on an agent pane and drops it from a shell"
    );
}

#[test]
fn finished_requests_fold() {
    let none = HashSet::new();
    let running = item(&status("a", "running"), 0);
    assert!(
        !fold::is_folded(&running, &none) && !fold::is_folded(&item(&status("w", "waiting"), 1), &none),
        "a running or waiting request is always open"
    );
    // The same request finishes: it folds by itself, with no event of its own.
    for s in ["completed", "error", "stopped"] {
        assert!(fold::is_folded(&item(&status("a", s), 0), &none), "a finished request folds: {s}");
    }
    assert!(
        !fold::is_folded(&item(&run("b", None, "completed", vec![agent("x", None, "running", None)], None, None), 0), &none),
        "a request whose sub-agent still runs is not finished yet"
    );
    let items = [running.clone(), item(&done("c"), 1), item(&status("d", "error"), 2)];
    assert!(
        fold::folded(items.iter(), &HashSet::from(["d".to_string()])) == HashSet::from(["c".to_string()]),
        "the folded cards are the finished ones not opened by hand"
    );

    let finished = item(&done("a"), 0);
    let opened = fold::toggled(&finished, &none);
    assert!(opened == HashSet::from(["a".to_string()]) && !fold::is_folded(&finished, &opened), "a card opened by hand stays open");
    // A later update of the same finished request keeps the user's choice.
    assert!(
        !fold::is_folded(&item(&run("a", None, "completed", vec![], Some("다시 끝났습니다"), None), 0), &opened),
        "a later update keeps the user's choice"
    );
    let closed = fold::toggled(&finished, &opened);
    assert!(closed.is_empty() && fold::is_folded(&finished, &closed), "a card folds back by hand");
    // A running card has no fold control: toggling it changes nothing, and it still folds when it finishes.
    let live = item(&status("b", "running"), 1);
    assert!(
        fold::toggled(&live, &none).is_empty() && fold::is_folded(&item(&done("b"), 1), &fold::toggled(&live, &none)),
        "a running card has no fold control"
    );

    assert_eq!(
        fold::result_line(&item(&run("a", None, "completed", vec![], Some("\n## **결론**: 끝났습니다\n\n- 둘째 줄"), None), 0)).as_deref(),
        Some("결론: 끝났습니다"),
        "a folded card shows the answer's first line without its Markdown marks"
    );
    // A failed or stopped request says so instead of its last words.
    assert!(
        fold::result_line(&item(&run("e", None, "error", vec![], Some("실패 이유"), None), 0)).is_none()
            && fold::result_line(&item(&status("s", "stopped"), 0)).is_none()
            && fold::result_line(&item(&done("n"), 0)).is_none(),
        "a request that failed, stopped or has no answer has no result line"
    );
    assert!(
        fold::first_line("```swift\n1. `let` x\n").as_deref() == Some("let x")
            && fold::first_line("> * 인용 목록").as_deref() == Some("인용 목록")
            && fold::first_line("  \n\t\n").is_none(),
        "the first line drops a fence's opening, a list's number, a quote and a list mark"
    );
}

fn at(origin: f64, document: f64, newest: Option<f64>, visible: f64, id: Option<&str>) -> Page {
    Page::new(origin, visible, document, id, newest)
}

fn page(origin: f64, document: f64, newest: f64) -> Page {
    at(origin, document, Some(newest), 600.0, Some("b"))
}

#[test]
fn dragging_empty_space_pans() {
    let minimum = (0.0, 0.0);
    let maximum = (240.0, 1_800.0);
    // The pointer moves 40 left and 70 down: the page shows 40 more to the right and 70 more above.
    assert!(
        pan::origin((100.0, 900.0), (300.0, 200.0), (260.0, 270.0), minimum, maximum) == (140.0, 830.0)
            && pan::origin((100.0, 900.0), (300.0, 200.0), (300.0, 200.0), minimum, maximum) == (100.0, 900.0),
        "the grabbed point stays under the pointer"
    );
    assert!(
        pan::origin((20.0, 1_750.0), (300.0, 300.0), (600.0, -500.0), minimum, maximum) == (0.0, 1_800.0)
            && pan::origin((20.0, 1_750.0), (300.0, 300.0), (-900.0, 4_000.0), minimum, maximum) == (240.0, 0.0),
        "the page stops at its edges"
    );
    assert_eq!(
        pan::origin((0.0, 0.0), (300.0, 300.0), (400.0, 400.0), (-12.0, -8.0), maximum),
        (-12.0, -8.0),
        "a page offset by its insets stops at its own limits"
    );
    // Narrower than the pane: only y moves. A page with no valid range keeps its origin.
    assert!(
        pan::origin((0.0, 500.0), (0.0, 0.0), (-80.0, -60.0), (0.0, 0.0), (0.0, 1_000.0)) == (0.0, 560.0)
            && pan::origin((5.0, 5.0), (0.0, 0.0), (-80.0, -60.0), (10.0, f64::NAN), (0.0, 100.0)) == (5.0, 5.0),
        "an axis that cannot move keeps its origin"
    );
    assert!(
        pan::scrollable((0.0, 0.0), (0.0, 1_000.0)) && !pan::scrollable((0.0, 0.0), (0.2, 0.4)),
        "a page is scrollable when either axis can move"
    );
    assert!(pan::may_pan(Target::Page, false, true), "an unmodified press on a movable page may pan");
    // Text selects, a scroll bar scrolls, an inner scroll view keeps its own scroll, and the composer or another pane is not this page.
    for target in [Target::TextOrControl, Target::InnerScroll, Target::Outside] {
        assert!(!pan::may_pan(target, false, true), "only the page pans: {target:?}");
    }
    assert!(
        !pan::may_pan(Target::Page, true, true) && !pan::may_pan(Target::Page, false, false),
        "a modified press, or a page that cannot move, keeps the press"
    );
    assert!(
        !pan::is_drag((100.0, 100.0), (100.0, 100.0))
            && !pan::is_drag((100.0, 100.0), (102.0, 98.0))
            && pan::is_drag((100.0, 100.0), (103.0, 100.0))
            && pan::is_drag((100.0, 100.0), (100.0, 96.0)),
        "a short press is a click, not a drag"
    );

    // The page follows the newest card at its end, 2,000pt tall in a 600pt pane.
    let mut watch = PageWatch::new();
    let at_y = |origin: f64, document: f64, newest: f64| page(origin, document, newest);
    assert!(watch.observe(&at_y(1_400.0, 2_000.0, 1_992.0), false).is_none() && watch.following(), "the page follows at its end");
    // The user drags the page down by 200pt: it moves up, a user move.
    let dragged = pan::origin((0.0, 1_400.0), (50.0, 50.0), (50.0, 250.0), (0.0, 0.0), (0.0, 1_400.0));
    assert!(
        dragged.1 == 1_200.0 && watch.observe(&at_y(dragged.1, 2_000.0, 1_992.0), true).is_none() && !watch.following(),
        "a drag away stops following"
    );
    // The newest card grows while the drag goes on and after it: nothing pulls the page back.
    assert!(
        watch.observe(&at_y(dragged.1, 2_300.0, 2_292.0), true).is_none()
            && watch.observe(&at_y(dragged.1, 2_400.0, 2_392.0), false).is_none(),
        "a pan is never fought"
    );
    // Dragged back to the newest card: following again, by the scroll rule.
    let back = pan::origin(dragged, (50.0, 250.0), (50.0, -550.0), (0.0, 0.0), (0.0, 1_800.0));
    assert!(
        back.1 == 1_800.0 && watch.observe(&at_y(back.1, 2_400.0, 2_392.0), true).is_none() && watch.following(),
        "dragged back to the newest card, the page follows again"
    );
}

#[test]
fn a_drag_in_a_column_moves_the_column_up_and_down_and_the_page_sideways() {
    // The board's page scrolls sideways only, each column up and down only.
    let page_range = pan::confined((0.0, 0.0), (240.0, 30.0), (100.0, 0.0), Axis::Horizontal);
    let column = pan::confined((0.0, 0.0), (4.0, 1_800.0), (0.0, 900.0), Axis::Vertical);
    assert!(
        page_range == ((0.0, 0.0), (240.0, 0.0)) && column == ((0.0, 0.0), (0.0, 1_800.0)),
        "the page keeps its height and a column its place sideways"
    );
    // The pointer moves 40 left and 70 down: the page shows 40 more to the right, the column 70 more above, each keeping its other axis.
    let (press, cursor) = ((300.0, 200.0), (260.0, 270.0));
    assert!(
        pan::origin((100.0, 0.0), press, cursor, page_range.0, page_range.1) == (140.0, 0.0)
            && pan::origin((0.0, 900.0), press, cursor, column.0, column.1) == (0.0, 830.0),
        "a drag moves the page by its horizontal part and the column by its vertical part"
    );
    // A column that cannot move, and a page that fits, keep every press.
    let still = pan::confined((0.0, 0.0), (0.0, 1_800.0), (0.0, 0.0), Axis::Horizontal);
    assert!(!pan::scrollable(still.0, still.1), "a page that fits cannot move up and down for a column");
}

#[test]
fn the_page_follows_the_newest_card() {
    let at_page = |origin: f64, document: f64, newest: Option<f64>| at(origin, document, newest, 600.0, Some("b"));
    // The newest card sits in a shorter column, 300pt above the end of the page; the reader scrolls all the way down.
    let mut watch = PageWatch::new();
    watch.observe(&at_page(2_000.0, 3_000.0, Some(2_500.0)), false);
    assert!(
        watch.observe(&at_page(2_400.0, 3_000.0, Some(2_700.0)), false).is_none() && watch.following(),
        "a reader at the true bottom still follows"
    );
    // The newest card grows while staying above the viewport's bottom: nothing moves.
    assert!(
        watch.observe(&at_page(2_400.0, 3_000.0, Some(2_760.0)), false).is_none()
            && watch.observe(&at_page(2_400.0, 3_000.0, Some(2_960.0)), false).is_none(),
        "a reader at the true bottom is never pulled up to the newest card"
    );

    // A card that grew by more than the slack in one layout pass is still followed.
    watch = PageWatch::new();
    watch.observe(&page(1_400.0, 2_000.0, 1_992.0), false);
    assert!(
        watch.following() && watch.observe(&page(1_400.0, 2_300.0, 2_292.0), false) == Some(Scroll::BOTTOM) && watch.following(),
        "a growth that hides the newest card is followed in one pass"
    );
    assert!(
        watch.observe(&page(1_700.0, 2_300.0, 2_292.0), false).is_none() && watch.following(),
        "the scroll that lands at the bottom keeps following"
    );

    watch = PageWatch::new();
    watch.observe(&page(1_000.0, 3_000.0, 1_590.0), false);
    assert!(
        watch.following() && watch.observe(&page(1_000.0, 3_000.0, 1_700.0), false) == Some(Scroll::to("b")),
        "a card in a shorter column is shown by its own bottom"
    );

    watch = PageWatch::new();
    watch.observe(&page(1_400.0, 2_000.0, 1_992.0), false);
    assert!(
        watch.observe(&page(300.0, 2_000.0, 1_992.0), false).is_none() && !watch.following(),
        "a reader who scrolled up no longer follows"
    );
    assert!(
        watch.observe(&page(300.0, 2_400.0, 2_392.0), false).is_none()
            && watch.observe(&at(300.0, 2_400.0, Some(2_392.0), 560.0, Some("b")), false).is_none(),
        "a reader who scrolled up stays where they are"
    );
    // Back at the newest card: following again.
    assert!(
        watch.observe(&at(1_800.0, 2_400.0, Some(2_392.0), 560.0, Some("b")), false).is_none() && watch.following(),
        "a reader who comes back to the newest card follows again"
    );

    // The page grows while the user's wheel is moving it up.
    watch = PageWatch::new();
    watch.observe(&page(1_400.0, 2_000.0, 1_992.0), false);
    assert!(watch.observe(&page(1_200.0, 2_300.0, 2_292.0), true).is_none() && !watch.following(), "a scroll in progress is never fought");

    watch = PageWatch::new();
    watch.observe(&page(1_400.0, 2_000.0, 1_992.0), false);
    assert_eq!(
        watch.observe(&at(1_400.0, 2_000.0, Some(1_992.0), 560.0, Some("b")), false),
        Some(Scroll::BOTTOM),
        "a taller composer keeps the newest card above it"
    );

    watch = PageWatch::new();
    watch.observe(&page(1_400.0, 2_000.0, 1_992.0), false);
    watch.observe(&page(200.0, 2_000.0, 1_992.0), false);
    assert!(
        !watch.following() && watch.new_request("c") == Scroll::to("c") && watch.following(),
        "a new request is shown and followed from then"
    );
    // The page lands short of it: still following, and shown again.
    assert!(
        watch.observe(&at(1_500.0, 2_400.0, Some(2_392.0), 600.0, Some("c")), false) == Some(Scroll::BOTTOM)
            && watch.observe(&at(1_800.0, 2_400.0, Some(2_392.0), 600.0, Some("c")), false).is_none()
            && watch.following(),
        "a new request is shown again when the page lands short of it"
    );

    watch = PageWatch::new();
    watch.observe(&page(1_400.0, 2_000.0, 1_992.0), false);
    watch.stop();
    assert!(!watch.following() && watch.observe(&page(1_400.0, 2_300.0, 2_292.0), false).is_none(), "opening a text stops following");

    // The page opens at its end, where the newest card is.
    watch = PageWatch::new();
    watch.stop();
    assert!(
        watch.appeared(Some("b")).is_none() && watch.observe(&page(1_400.0, 2_000.0, 1_992.0), false).is_none() && watch.following(),
        "a page reopened at its newest card follows again"
    );

    watch = PageWatch::new();
    assert_eq!(watch.appeared(Some("b")), Some(Scroll::to("b")), "coming back shows the newest card to a following reader");
    watch.stop();
    assert!(watch.appeared(Some("b")).is_none(), "coming back moves nothing for a reader who was reading");
    // An empty pane follows its page's end.
    let mut empty = PageWatch::new();
    assert!(empty.observe(&at(0.0, 40.0, None, 600.0, None), false).is_none() && empty.following(), "an empty pane follows its page's end");
}

#[test]
fn a_users_move_is_judged_when_it_happens() {
    let at_page = |origin: f64, document: f64, newest: f64, visible: f64| at(origin, document, Some(newest), visible, Some("b"));
    // Symptom (Mac CI, trackpad smoke): the page moved 180pt up from the newest card, yet kept following.
    let mut watch = PageWatch::new();
    watch.observe(&at_page(1_523.0, 2_100.0, 2_091.0, 577.0), false);
    assert!(watch.following(), "the page follows at the newest card");
    let mut blind = PageWatch::new();
    blind.observe(&at_page(1_523.0, 2_100.0, 2_091.0, 577.0), false);
    assert!(
        blind.observe(&at_page(1_343.0, 2_130.0, 2_121.0, 577.0), false) == Some(Scroll::BOTTOM) && blind.following(),
        "a late report with no move known pulls the reader back"
    );
    // The page tells the rule the moment it moves.
    watch.user_scrolled(-180.0);
    assert!(
        !watch.following() && watch.observe(&at_page(1_343.0, 2_130.0, 2_121.0, 577.0), false).is_none() && !watch.following(),
        "a user's move is judged when it happens, and the late report keeps the reader where they are"
    );

    watch = PageWatch::new();
    watch.observe(&at_page(1_400.0, 2_000.0, 1_992.0, 600.0), false);
    // 20pt up: the newest card's bottom is hidden by less than the slack. A sideways move keeps the state it had.
    watch.user_scrolled(-20.0);
    assert!(watch.following(), "a move near the newest card keeps following");
    watch.user_scrolled(0.0);
    assert!(watch.following(), "a sideways move keeps the state");
    // Its report is judged as the scroll it is: still at the newest card, nothing moves. A growth after it is followed.
    assert!(
        watch.observe(&at_page(1_380.0, 2_000.0, 1_992.0, 600.0), false).is_none()
            && watch.observe(&at_page(1_380.0, 2_300.0, 2_292.0, 600.0), false) == Some(Scroll::BOTTOM)
            && watch.following(),
        "the move's report is a scroll, and a growth after it is followed"
    );

    watch = PageWatch::new();
    watch.user_scrolled(-300.0);
    assert!(watch.following(), "a move before any report judges nothing");
    watch.observe(&at_page(1_400.0, 2_000.0, 1_992.0, 600.0), false);
    watch.user_scrolled(-300.0);
    assert!(!watch.following(), "a move away stops following");
    watch.user_scrolled(300.0);
    assert!(watch.following(), "a move back to the newest card follows again");
}

/// A column 600pt tall whose last card ends 8pt above its document's end.
fn column(id: &str, origin: f64, document: f64) -> Page {
    Page::new(origin, 600.0, document, Some(id), Some(document - 8.0))
}

#[test]
fn each_column_follows_its_own_end() {
    // The 실행 column at its end as tool calls arrive: shown to its new end each time.
    let mut watch = ColumnWatch::new();
    assert_eq!(
        watch.appeared("b", true, "execution"),
        Some(Scroll::to("execution")),
        "a column appearing on the newest board shows its end"
    );
    assert!(
        watch.observe("b", true, "execution", &column("execution", 1_400.0, 2_000.0), false).is_none()
            && watch.observe("b", true, "execution", &column("execution", 1_400.0, 2_300.0), false) == Some(Scroll::BOTTOM)
            && watch.following("execution"),
        "a column at its end follows its own growth"
    );
    // A short column at its top is at its end too, and follows.
    assert!(
        watch.observe("b", true, "result", &column("result", 0.0, 200.0), false).is_none() && watch.following("result"),
        "a short column follows"
    );

    watch = ColumnWatch::new();
    watch.observe("b", true, "execution", &column("execution", 1_400.0, 2_000.0), false);
    watch.observe("b", true, "request", &column("request", 900.0, 1_500.0), false);
    // A wheel moves the 실행 column 200pt up: it stops; the 요청 column follows on.
    watch.user_scrolled("execution", -200.0);
    assert!(!watch.following("execution") && watch.following("request"), "a user's move stops its column only");
    // Both grow: only the 요청 column is shown to its end; the reader of 실행 stays.
    assert!(
        watch.observe("b", true, "execution", &column("execution", 1_200.0, 2_400.0), false).is_none()
            && watch.observe("b", true, "request", &column("request", 900.0, 1_800.0), false) == Some(Scroll::BOTTOM),
        "only the following column is shown to its end"
    );
    // A scroll the system reports in one column is that column's.
    assert!(
        watch.observe("b", true, "request", &column("request", 600.0, 1_800.0), true).is_none() && !watch.following("request"),
        "a reported scroll stops its own column"
    );
    // Coming back to its end resumes the column's follow.
    assert!(
        watch.observe("b", true, "execution", &column("execution", 1_800.0, 2_400.0), false).is_none()
            && watch.following("execution")
            && !watch.following("request"),
        "a column back at its end follows again"
    );

    watch = ColumnWatch::new();
    watch.observe("b", true, "execution", &column("execution", 1_400.0, 2_000.0), false);
    watch.stop("request");
    assert!(!watch.following("request") && watch.following("execution"), "opening a text stops its column only");
    assert!(
        watch.observe("b", true, "request", &column("request", 0.0, 1_200.0), false).is_none()
            && watch.observe("b", true, "execution", &column("execution", 1_400.0, 2_300.0), false) == Some(Scroll::BOTTOM),
        "the other column still follows"
    );
}

#[test]
fn a_text_opened_in_a_following_column_leaves_the_reader_where_it_begins() {
    let following = || {
        let mut made = ColumnWatch::new();
        assert!(
            made.observe("b", true, "request", &column("request", 0.0, 300.0), false).is_none() && made.following("request"),
            "a short column at its end follows"
        );
        made
    };
    let mut watch = following();
    let mut late = following();
    watch.stop("request");
    assert!(
        watch.observe("b", true, "request", &column("request", 0.0, 1_200.0), false).is_none()
            && watch.observe("b", true, "request", &column("request", 0.0, 1_200.0), false).is_none()
            && !watch.following("request"),
        "a column stopped before its text grew moves for none of its reports"
    );
    assert_eq!(
        late.observe("b", true, "request", &column("request", 0.0, 1_200.0), false),
        Some(Scroll::BOTTOM),
        "a report before the stop asks for the column's end"
    );
    late.stop("request");
    assert!(!late.following("request"), "the stop right after it leaves the column not following");
    assert!(
        watch.observe("b", true, "request", &column("request", 600.0, 1_200.0), true).is_none() && watch.following("request"),
        "the reader scrolling down to the text's end follows again"
    );
}

#[test]
fn a_new_request_or_another_board_starts_the_columns_over() {
    let mut watch = ColumnWatch::new();
    watch.observe("p", true, "plan", &column("plan", 1_400.0, 2_000.0), false);
    watch.user_scrolled("plan", -500.0);
    watch.stop("execute");
    assert!(!watch.following("plan") && !watch.following("execute"), "the reader left both columns");
    // A styled pipeline's next request: the same board, every column back at its end.
    assert!(
        watch.new_request("p", "plan") == Scroll::to("plan")
            && watch.new_request("p", "execute") == Scroll::to("execute")
            && watch.following("plan")
            && watch.following("execute"),
        "a new request shows every column's end"
    );
    // The column lands short of its end at first: still following, and shown again.
    assert!(
        watch.observe("p", true, "plan", &column("plan", 900.0, 2_400.0), false) == Some(Scroll::BOTTOM)
            && watch.observe("p", true, "plan", &column("plan", 1_800.0, 2_400.0), false).is_none()
            && watch.following("plan"),
        "a column that lands short of its end is shown again"
    );

    watch = ColumnWatch::new();
    watch.observe("new", true, "execution", &column("execution", 1_400.0, 2_000.0), false);
    watch.user_scrolled("execution", -300.0);
    // An older board opens at its columns' tops: none follows, and its growth moves nothing.
    assert!(
        watch.appeared("old", false, "execution").is_none() && !watch.following("execution") && !watch.following("result"),
        "an older board follows nothing"
    );
    assert!(
        watch.observe("old", false, "execution", &column("execution", 0.0, 2_000.0), false).is_none(),
        "an older board's growth moves nothing"
    );
    watch.stop("execution");
    watch.user_scrolled("execution", 100.0);
    assert_eq!(watch.board_id(), Some("old"), "the older board is the one shown");
    // Back to the newest board: every column shows its end and follows, whatever the reader did before.
    assert!(
        watch.appeared("new", true, "execution") == Some(Scroll::to("execution"))
            && watch.appeared("new", true, "result") == Some(Scroll::to("result"))
            && watch.following("execution"),
        "the newest board follows every column again"
    );
    // The board shown stops being the newest when a newer one arrives: it follows nothing from then.
    assert!(
        watch.observe("new", false, "execution", &column("execution", 1_400.0, 2_300.0), false).is_none() && !watch.following("execution"),
        "a board that is no longer the newest follows nothing"
    );

    watch = ColumnWatch::new();
    watch.observe("b", true, "execution", &column("execution", 1_400.0, 2_000.0), false);
    watch.observe("b", true, "request", &column("request", 0.0, 300.0), false);
    watch.user_scrolled("execution", -400.0);
    // The view shows the diagram and comes back: the same board, its columns appear again.
    assert!(
        watch.appeared("b", true, "execution").is_none() && watch.appeared("b", true, "request") == Some(Scroll::to("request")),
        "coming back shows only the following columns' ends"
    );
    // The reader of 실행 is judged by where the column opened.
    assert!(
        watch.observe("b", true, "execution", &column("execution", 1_400.0, 2_000.0), false).is_none() && watch.following("execution"),
        "a column reopened at its end follows again"
    );
}
