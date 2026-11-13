//! The role view's session log and the words the web page gets (C# FlowGraphVerification, MightyCode.Core.Tests/FlowGraphVerification.cs).

mod view_support;

use std::collections::HashMap;

use chrono::FixedOffset;
use mighty_core::Wire;
use mighty_core::graph::{FlowStep, GraphAgentFigures, MightyGraphAgent, MightyGraphRun};
use mighty_core::json::JsonOut;
use mighty_core::model::{LogEntry, RunSession};
use mighty_core::time::DateTimeOffset;
use mighty_graph::flow::copy;
use mighty_graph::flow::log::{self, Actor, Part, Row};
use mighty_graph::flow::roles::{self, AgentDefinition, FlowInk, FlowRole};
use mighty_graph::flow::steps;
use mighty_graph::layout::{self, Options};
use mighty_graph::{labels, support};
use view_support::flow::{self, a, node_of, r};
use view_support::*;

#[derive(Default, Clone)]
struct G<'a> {
    parent: Option<&'a str>,
    status: Option<&'a str>,
    background: bool,
    kind: Option<&'a str>,
    source: Option<&'a str>,
    entries: Vec<LogEntry>,
    started: Option<i64>,
    ended: Option<i64>,
    agent_type: Option<&'a str>,
    resolved: Option<&'a str>,
}

/// FlowGraphVerification's own `Agent(id, parent, status, background, kind, source, entries, started, ended, type, resolved)`.
fn agent_of(id: &str, g: G) -> MightyGraphAgent {
    let mut b =
        a(id).status(g.status.unwrap_or("running")).entries(g.entries).identity(identity(g.agent_type, None, g.resolved, None)).figures(
            GraphAgentFigures {
                started_at: g.started.map(flow::stamp),
                ended_at: g.ended.map(flow::stamp),
                tool_calls: None,
                exit_code: None,
            },
        );
    if let Some(p) = g.parent {
        b = b.parent(p);
    }
    if g.background {
        b = b.background();
    }
    if let Some(k) = g.kind {
        b = b.kind(k);
    }
    if let Some(s) = g.source {
        b = b.source(s);
    }
    b.build()
}

fn words(rows: &[Row]) -> Vec<String> {
    rows.iter().map(|r| format!("{} | {}", r.who, r.what)).collect()
}

fn strings(list: &[&str]) -> Vec<String> {
    list.iter().map(|s| s.to_string()).collect()
}

fn none() -> HashMap<String, AgentDefinition> {
    HashMap::new()
}

fn dates(list: &[i64]) -> Vec<DateTimeOffset> {
    list.iter().map(|s| flow::date(*s)).collect()
}

/// "flow log: the rows are what the app saw happen" (TheLogsRowsAreWhatTheAppSawHappen).
#[test]
fn the_logs_rows_are_what_the_app_saw_happen() {
    let ko = ko();
    let root = vec![
        flow::said("t1", "Looking around.", 1),
        flow::tool("r1", "Sources/A.swift 읽기", 2, "Read", "tool", "completed"),
        flow::tool("spawn", "Agent: scout", 3, "Agent", "tool", "completed"),
        flow::tool("turn", "응답 생성", 4, "", "turn", "completed"),
        flow::tool("ask", "어느 쪽으로 할까요?", 9, "AskUserQuestion", "tool", "waiting"),
        flow::said_as("c1", &format!("{} · 자동 정리 · 84,120 → 21,300 토큰", labels::compaction_title(&ko)), 10, "system"),
        flow::said_as("e1", "오류 줄", 11, "error"),
    ];
    let scout = agent_of(
        "scout",
        G {
            status: Some("completed"),
            entries: vec![
                flow::tool("s1", "grep tracker", 5, "Grep", "tool", "completed"),
                flow::said("s2", "Found three.", 6),
                flow::tool("s3", "B.swift 읽기", 7, "Read", "tool", "completed"),
            ],
            started: Some(3),
            ended: Some(8),
            agent_type: Some("Explore"),
            resolved: Some("claude-haiku-5-5"),
            ..G::default()
        },
    );
    let request = r("r").root(root).agents(vec![scout]).build();
    let rows = log::rows(std::slice::from_ref(&request), 20, &none(), &ko);
    same(
        words(&rows),
        strings(&[
            "Sonnet 5.5 | Sources/A.swift 읽기",
            "scout | 시작 · Explore · Haiku 5.5",
            "scout | grep tracker",
            "scout | B.swift 읽기",
            "scout | 완료",
            "Sonnet 5.5 | 질문 · 어느 쪽으로 할까요?",
            "Sonnet 5.5 | 컨텍스트 정리 · 자동 정리 · 84,120 → 21,300 토큰",
        ]),
        "the rows",
    );
    same(rows.iter().map(|r| r.time).collect::<Vec<_>>(), dates(&[2, 3, 5, 7, 8, 9, 10]), "their times");
    check(
        rows[0].actor == Actor { role: FlowRole::Lead, agent_id: None } && rows[0].ink == roles::ink(FlowRole::Lead),
        "the lead in its ink",
    );
    same(rows[0].node_id.clone(), node_of(&request, layout::WORK_SUFFIX), "leading to the 실행 block");
    check(
        rows[1].actor == Actor { role: FlowRole::Subagent, agent_id: Some("scout".into()) } && rows[1].ink == FlowInk::Agent,
        "the sub-agent in its ink",
    );
    same(rows[1].node_id.clone(), node_of(&request, "agent:scout"), "leading to its block");
    check(rows[1].actor.role == FlowRole::Subagent && rows[0].actor.role == FlowRole::Lead, "the actors' roles");
    same(rows.iter().map(|r| r.id.clone()).collect::<std::collections::HashSet<_>>().len(), rows.len(), "an id each");

    same(log::lead_words(&flow::said("t", "Just words.", 0), &ko), None, "a reply");
    same(log::lead_words(&flow::tool("turn", "응답 생성", 0, "", "turn", "completed"), &ko), None, "the lifecycle row");
    same(log::lead_words(&flow::tool("a", "Agent: scout", 0, "Agent", "tool", "completed"), &ko), None, "an Agent call");
    same(log::lead_words(&flow::tool("a", "Task: scout", 0, "Task", "tool", "completed"), &ko), None, "a Task call");
    same(log::lead_words(&flow::said_as("e", "오류", 0, "error"), &ko), None, "an error line");
    same(log::lead_words(&flow::said_as("s", "다른 시스템 줄", 0, "system"), &ko), None, "another system line");
    same(
        log::lead_words(&flow::tool("w", "", 0, "WebFetch", "tool", "completed"), &ko).as_deref(),
        Some("WebFetch"),
        "a call without a summary",
    );
    let longest = log::lead_words(&flow::read("l", &format!("{}\nnext", "가".repeat(400))), &ko).unwrap_or_default();
    check(longest.len() <= 240 && !longest.contains('\n'), "one bounded line");

    let busy = agent_of(
        "busy",
        G {
            entries: (0..40).map(|i| flow::tool(&format!("b{i}"), &format!("grep {i}"), i * 2 + 1, "Read", "tool", "completed")).collect(),
            started: Some(0),
            ..G::default()
        },
    );
    let many = r("r")
        .root((0..40).map(|i| flow::tool(&format!("r{i}"), &format!("read {i}"), i * 2, "Read", "tool", "completed")).collect())
        .agents(vec![busy])
        .build();
    let last = log::rows(std::slice::from_ref(&many), log::VISIBLE_ROWS, &none(), &ko);
    same(last.len(), log::VISIBLE_ROWS, "the last five");
    same(
        last.iter().map(|r| r.what.clone()).collect::<Vec<_>>(),
        strings(&["grep 37", "read 38", "grep 38", "read 39", "grep 39"]),
        "oldest first",
    );
    let times: Vec<i64> = last.iter().map(|r| r.time.utc_ticks()).collect();
    let mut sorted = times.clone();
    sorted.sort();
    same(times, sorted, "in time");
    same(
        log::rows(std::slice::from_ref(&many), 2, &none(), &ko).iter().map(|r| r.what.clone()).collect::<Vec<_>>(),
        strings(&["read 39", "grep 39"]),
        "a smaller limit",
    );
    same(log::rows(std::slice::from_ref(&many), 0, &none(), &ko).len(), 0, "no room");
    same(log::rows(&[], log::VISIBLE_ROWS, &none(), &ko).len(), 0, "no run");

    let mut second = r("r")
        .root(vec![
            flow::tool("a", "first", 5, "Read", "tool", "completed"),
            flow::tool("b", "second", 5, "Read", "tool", "completed"),
            flow::tool("c", "third", 5, "Read", "tool", "completed"),
        ])
        .build();
    same(
        log::rows(std::slice::from_ref(&second), log::VISIBLE_ROWS, &none(), &ko).iter().map(|r| r.what.clone()).collect::<Vec<_>>(),
        strings(&["first", "second", "third"]),
        "one second keeps the recorded order",
    );
    second.root_entries.as_mut().unwrap()[1].timestamp = Some("not a time".into());
    same(
        log::rows(std::slice::from_ref(&second), log::VISIBLE_ROWS, &none(), &ko).iter().map(|r| r.what.clone()).collect::<Vec<_>>(),
        strings(&["first", "third"]),
        "a time that cannot be read is no row",
    );

    let old = r("old")
        .status("completed")
        .root(vec![flow::tool("o1", "old read", 1, "Read", "tool", "completed"), flow::said("o2", "old answer", 2)])
        .output("old answer")
        .build();
    let bg = agent_of(
        "bg",
        G {
            status: Some("completed"),
            background: true,
            entries: vec![flow::tool("g1", "c.txt 읽기", 13, "Read", "tool", "completed")],
            started: Some(12),
            ended: Some(15),
            ..G::default()
        },
    );
    let request = r("new")
        .status("completed")
        .agents(vec![bg])
        .root(vec![flow::tool("n1", "new read", 11, "Read", "tool", "completed"), flow::said("n2", "launched", 14)])
        .output("launched")
        .build();
    let copy_block = || {
        agent_of(
            "new:notice:1:bg",
            G { status: Some("completed"), background: true, source: Some("bg"), started: Some(12), ended: Some(15), ..G::default() },
        )
    };
    let notice_of = |status: &str, rooted: bool, output: Option<&str>| {
        let mut b = r("new:notice:1").status(status).agents(vec![copy_block()]).notice().title("백그라운드 작업 완료 · bg");
        if rooted {
            b = b.root(vec![flow::said("x1", "done", 16)]);
        }
        if let Some(o) = output {
            b = b.output(o);
        }
        b.build()
    };
    let notice = notice_of("completed", true, Some("done"));
    let runs = support::shown_runs(&[old.clone(), request.clone(), notice.clone()], &ko).into_owned();
    same(log::activity(&runs), (1, 3), "the newest request and the notice runs after it");
    let rows = log::rows(&runs, 20, &none(), &ko);
    same(
        words(&rows),
        strings(&[
            "Sonnet 5.5 | new read",
            "bg | 시작",
            "bg | c.txt 읽기",
            "Sonnet 5.5 | 완료",
            "bg | 완료",
            "Sonnet 5.5 | 알림 받음 · 백그라운드 작업 완료 · bg",
            "Sonnet 5.5 | 완료",
        ]),
        "the activity's rows",
    );
    same(rows.iter().map(|r| r.time).collect::<Vec<_>>(), dates(&[11, 12, 13, 14, 15, 16, 16]), "their times");
    same(rows[5].node_id.clone(), node_of(&notice, "request"), "the notice leads to its run's request block");
    same(rows[6].node_id.clone(), node_of(&notice, layout::WORK_SUFFIX), "its end to its 실행 block");
    let answering =
        log::rows(&support::shown_runs(&[old.clone(), request.clone(), notice_of("running", false, None)], &ko), 20, &none(), &ko);
    check(
        answering.last().is_some_and(|r| r.what == "알림 받음 · 백그라운드 작업 완료 · bg" && r.time == flow::date(15)),
        "a notice that recorded nothing yet came when its work ended",
    );
    same(
        log::rows(&support::shown_runs(&[old.clone(), request.clone(), notice_of("completed", true, None)], &ko), 20, &none(), &ko)
            .last()
            .map(|r| r.what.clone())
            .as_deref(),
        Some("알림 받음 · 백그라운드 작업 완료 · bg"),
        "taken into a running turn: no end",
    );
    same(
        log::rows(&support::shown_runs(&[old, request, notice_of("error", true, None)], &ko), 20, &none(), &ko)
            .last()
            .map(|r| r.what.clone())
            .as_deref(),
        Some("실패"),
        "one that failed says so",
    );
    same(log::activity(std::slice::from_ref(&notice)), (0, 1), "a pane with only a notice run");

    let plain = agent_of(
        "plain",
        G { status: Some("completed"), entries: vec![flow::tool("p1", "read", 3, "Read", "tool", "completed")], ..G::default() },
    );
    let custom = agent_of(
        "custom",
        G { entries: vec![flow::tool("c1", "grep", 4, "Read", "tool", "completed")], agent_type: Some("reviewer"), ..G::default() },
    );
    let nested = agent_of(
        "nested",
        G { parent: Some("custom"), entries: vec![flow::tool("d1", "deep", 5, "Read", "tool", "completed")], ..G::default() },
    );
    let task = agent_of(
        "task",
        G { background: true, kind: Some("task"), entries: vec![flow::said_as("t1", "launched", 6, "system")], ..G::default() },
    );
    let definitions =
        HashMap::from([("reviewer".to_string(), AgentDefinition { name: "reviewer".into(), color: Some("green".into()), model: None })]);
    let inked = log::rows(&[r("r").agents(vec![plain, custom, nested, task]).build()], 20, &definitions, &ko);
    same(
        inked.iter().map(|r| r.what.clone()).collect::<Vec<_>>(),
        strings(&["read", "grep", "deep"]),
        "no start or end without figures, no command",
    );
    same(inked.iter().map(|r| r.ink).collect::<Vec<_>>(), vec![FlowInk::Agent, FlowInk::Done, FlowInk::Agent], "a custom agent's colour");
    same(inked.iter().map(|r| r.who.clone()).collect::<Vec<_>>(), strings(&["plain", "custom", "nested"]), "by their names");

    same(log::lead_name(&r("r").build(), None, &ko).as_str(), "Sonnet 5.5", "the model that answered");
    let mut configured = r("r").model(None).build();
    configured.node_model_label = Some("opus · 설정".into());
    same(log::lead_name(&configured, None, &ko).as_str(), "Opus · 설정", "the configured model");
    same(log::lead_name(&r("r").model(None).build(), None, &ko).as_str(), "Claude", "the agent's name");
    same(log::lead_name(&r("r").model(None).provider("codex").build(), None, &ko).as_str(), "Codex", "another agent's");

    let inner = agent_of(
        "inner",
        G {
            parent: Some("outer"),
            entries: vec![flow::tool("i1", "grep", 4, "Grep", "tool", "completed")],
            started: Some(3),
            ..G::default()
        },
    );
    let outer =
        agent_of("outer", G { entries: vec![flow::tool("o1", "ls", 2, "Bash", "tool", "completed")], started: Some(1), ..G::default() });
    let target_run = r("r").root(vec![flow::tool("m1", "A.swift 읽기", 0, "Read", "tool", "completed")]).agents(vec![outer, inner]).build();
    let target_rows = log::rows(std::slice::from_ref(&target_run), 20, &none(), &ko);
    let inner_row = target_rows.iter().find(|r| r.id.ends_with(":i1")).unwrap();
    let lead_row = target_rows.iter().find(|r| r.actor.role == FlowRole::Lead).unwrap();
    let (work_node, outer_node, inner_node) =
        (node_of(&target_run, layout::WORK_SUFFIX), node_of(&target_run, "agent:outer"), node_of(&target_run, "agent:inner"));
    let runs = std::slice::from_ref(&target_run);
    same(inner_row.node_id.clone(), inner_node.clone(), "its block");
    same(log::target(inner_row, runs, |_| true), Some(inner_node.clone()), "its own when drawn");
    same(log::target(lead_row, runs, |_| true), Some(work_node.clone()), "the lead's");
    same(log::target(inner_row, runs, |id| id != inner_node), Some(outer_node.clone()), "the card above it");
    same(log::target(inner_row, runs, |id| id == work_node), Some(work_node.clone()), "else the main session");
    same(log::target(inner_row, runs, |_| false), None, "nothing drawn");
    same(log::target(lead_row, runs, |_| false), None, "nothing drawn for the lead");
    let looped = r("r").agents(vec![agent_of("self", G { parent: Some("self"), started: Some(1), ..G::default() })]).build();
    let loop_rows = log::rows(std::slice::from_ref(&looped), 20, &none(), &ko);
    same(log::target(&loop_rows[0], std::slice::from_ref(&looped), |_| false), None, "a block that is its own parent ends the walk");
    let folded: std::collections::HashSet<String> = flow::flow_layout(runs, true, &[], true).nodes.into_iter().map(|n| n.id).collect();
    same(log::target(inner_row, runs, |id| folded.contains(id)), Some(outer_node.clone()), "the layout the pane draws: the folded card");
    same(log::target(lead_row, runs, |id| folded.contains(id)), Some(work_node.clone()), "and the 실행 block");
    let opened: std::collections::HashSet<String> =
        flow::flow_layout(runs, true, std::slice::from_ref(&outer_node), true).nodes.into_iter().map(|n| n.id).collect();
    same(log::target(inner_row, runs, |id| opened.contains(id)), Some(inner_node), "opened, its own");

    let moment = DateTimeOffset::from_utc_ticks(621_355_968_000_000_000 + 1_794_336_483 * 10_000_000);
    same(log::clock(&moment, FixedOffset::east_opt(0)).as_str(), "18:48:03", "in UTC");
    same(log::clock(&moment, FixedOffset::east_opt(9 * 3600)).as_str(), "03:48:03", "nine hours east");
}

/// "flow log: the status line says who is doing what now" (TheStatusLineSaysWhoIsDoingWhatNow).
#[test]
fn the_status_line_says_who_is_doing_what_now() {
    let ko = ko();
    let agents = vec![
        agent_of("a", G::default()),
        agent_of("b", G { status: Some("completed"), ..G::default() }),
        agent_of("c", G { background: true, ..G::default() }),
    ];
    let running = log::status_of(&[r("r").agents(agents).build()], &ko).unwrap();
    same(running.lead.as_str(), "Sonnet 5.5 실행 중", "the main session");
    same(running.subagents.as_deref(), Some("하위 에이전트 2/3 실행 중"), "the sub-agents");
    same(running.text().as_str(), "Sonnet 5.5 실행 중 · 하위 에이전트 2/3 실행 중", "the line");
    same(
        running.parts(),
        vec![
            Part { role: FlowRole::Lead, text: "Sonnet 5.5 실행 중".into() },
            Part { role: FlowRole::Subagent, text: "하위 에이전트 2/3 실행 중".into() },
        ],
        "its parts",
    );
    let ended = vec![
        agent_of("a", G { status: Some("completed"), ..G::default() }),
        agent_of("b", G { status: Some("completed"), ..G::default() }),
    ];
    same(
        log::status_of(&[r("r").status("completed").agents(ended).output("done").build()], &ko).unwrap().text().as_str(),
        "Sonnet 5.5 완료 · 하위 에이전트 2개 끝남",
        "every sub-agent ended",
    );
    same(log::status_of(&[r("r").status("error").build()], &ko).unwrap().text().as_str(), "Sonnet 5.5 실패", "failed");
    same(log::status_of(&[], &ko), None, "no request");
    let awaiting = support::shown_runs(
        &[r("r").status("completed").agents(vec![agent_of("bg", G { background: true, ..G::default() })]).output("launched").build()],
        &ko,
    )
    .into_owned();
    same(
        log::status_of(&awaiting, &ko).unwrap().text().as_str(),
        "Sonnet 5.5 알림 대기 · 하위 에이전트 1/1 실행 중",
        "awaiting its notice",
    );
    let request = || {
        r("r")
            .status("completed")
            .agents(vec![agent_of("bg", G { status: Some("completed"), background: true, ..G::default() })])
            .output("launched")
            .build()
    };
    let answer = |status: &str, output: Option<&str>, model: Option<&str>| {
        let mut b = r("r:notice:1")
            .status(status)
            .agents(vec![agent_of("r:notice:1:bg", G { status: Some("completed"), background: true, source: Some("bg"), ..G::default() })])
            .notice()
            .title("백그라운드 작업 완료 · bg")
            .model(model);
        if let Some(o) = output {
            b = b.output(o);
        }
        b.build()
    };
    let status = |runs: Vec<MightyGraphRun>| log::status_of(&support::shown_runs(&runs, &ko), &ko).unwrap();
    same(
        status(vec![request(), answer("running", None, Some("claude-haiku-5-5"))]).text().as_str(),
        "Haiku 5.5 실행 중 · 하위 에이전트 1개 끝남",
        "a notice turn is the main session at work",
    );
    same(
        status(vec![request(), answer("completed", Some("done"), Some("claude-haiku-5-5"))]).text().as_str(),
        "Haiku 5.5 완료 · 하위 에이전트 1개 끝남",
        "answered",
    );
    same(
        status(vec![request(), answer("running", None, None)]).lead.as_str(),
        "Sonnet 5.5 실행 중",
        "named as its request until it answers",
    );
}

fn pane_of(json: &str) -> RunSession {
    RunSession::read(&serde_json::from_str(json).unwrap()).unwrap()
}

fn written(pane: &RunSession) -> String {
    let mut out = JsonOut::new();
    pane.write(&mut out);
    out.finish()
}

/// "flow log: the recorded request logs its sub-agents' work, and the pane remembers the strip folded (claude-stream-subagents)"
/// (TheRecordedRequestLogsItsSubagentsWork, FlowGraphVerification.cs:199-226): the recording replayed through the run engine's stream
/// parser and the tracker (`ReplayParsed`), then the pane's half.
#[test]
fn the_recorded_request_logs_its_subagents_work() {
    let ko = ko();
    let runs = support::shown_runs(&replay_parsed("claude-stream-subagents", "recorded"), &ko).into_owned();
    let rows = log::rows(&runs, 40, &none(), &ko);
    let starts: Vec<&Row> = rows.iter().filter(|r| r.id.ends_with(":start")).collect();
    same(starts.iter().map(|r| r.who.as_str()).collect::<Vec<_>>(), vec!["count a", "count b and c"], "both started");
    same(
        starts.iter().map(|r| r.what.as_str()).collect::<Vec<_>>(),
        vec!["시작 · Explore · Haiku 5.5", "시작 · Explore · Haiku 5.5"],
        "as their cards' second line",
    );
    same(
        starts.iter().map(|r| r.time).collect::<Vec<_>>(),
        ["2026-11-10T18:56:31.912Z", "2026-11-10T18:56:44.496Z"]
            .iter()
            .map(|t| mighty_core::time::parse(Some(t)).unwrap())
            .collect::<Vec<_>>(),
        "at the times the CLI's frames name",
    );
    same(
        rows.iter().filter(|r| r.id.ends_with(":end") && r.actor.role != FlowRole::Lead).map(|r| r.who.as_str()).collect::<Vec<_>>(),
        vec!["count a", "count b and c"],
        "both ended",
    );
    let background = support::flow_agents(&runs[0]).into_iter().find(|a| a.title() == "count b and c").unwrap().id().to_string();
    let theirs = Actor { role: FlowRole::Subagent, agent_id: Some(background) };
    same(
        rows.iter().filter(|r| r.actor == theirs && !r.id.ends_with(":start") && !r.id.ends_with(":end")).count(),
        2,
        "the background agent's two reads",
    );
    check(rows.iter().any(|r| r.what == "알림 받음 · 백그라운드 작업 완료 · count b and c"), "its end came in as a notice");
    same(log::status_of(&runs, &ko).unwrap().text().as_str(), "Haiku 5.5 완료 · 하위 에이전트 2개 끝남", "the status once it is over");

    let pane = pane_of(r#"{"id":"p","workspaceId":"w","title":"Claude"}"#);
    same(pane.flow_log_collapsed, None, "an older save is open");
    check(!written(&pane).contains("flowLogCollapsed"), "an open strip writes nothing");
    let folded = copy::collapse(&pane, true);
    same(pane_of(&written(&folded)).flow_log_collapsed, Some(true), "a folded one is kept");
    same(copy::collapse(&folded, false).flow_log_collapsed, None, "opened again");
    same(pane_of(r#"{"id":"p","workspaceId":"w","title":"Claude","flowLogCollapsed":false}"#).flow_log_collapsed, None, "false is open");
    same(pane_of(r#"{"id":"p","workspaceId":"w","title":"Claude","flowLogCollapsed":"yes"}"#).flow_log_collapsed, None, "damage is open");
}

/// "flow copy: the web page's copy is the role view's words" (TheWebPagesCopyIsTheRoleViewsWords).
#[test]
fn the_web_pages_copy_is_the_role_views_words() {
    let ko = ko();
    let scout = a("scout")
        .identity(identity(Some("Explore"), Some("haiku"), None, None))
        .figures(figures(Some(&flow::stamp(1)), None, None, None))
        .build();
    let done = a("done").status("completed").entries(vec![flow::said("d1", "finished", 0)]).build();
    let build = a("ci")
        .background()
        .kind("task")
        .title("build")
        .input("npm run build")
        .figures(figures(Some(&flow::stamp(0)), None, None, None))
        .build();
    let request = r("r").agents(vec![scout, done, build]).effort("high").build();
    let runs = vec![request.clone()];
    let made = flow::flow_layout(&runs, true, &[], true);
    let output = |id: &str| if id == "ci" { vec!["compiling".to_string(), "[exited with code 0]".to_string()] } else { vec![] };
    let blocks = copy::blocks(&made, &runs, true, &none(), &output, &ko);
    let work_copy = &blocks[&node_of(&request, layout::WORK_SUFFIX)];
    let work = work_copy.role.as_ref().unwrap();
    check(
        work.name == "lead"
            && work.ink == "taskText"
            && work.effort.as_deref() == Some("High")
            && work.effort_help.as_deref() == Some("사고 강도 · High"),
        "the 실행 block is the lead with its effort",
    );
    let card = work_copy.card.as_ref().unwrap();
    check(
        card.title == "실행 · 요청 1" && card.identity.as_deref() == Some("Sonnet 5.5") && card.running && card.ink == "taskText",
        "and the 실행 card",
    );
    let scout_copy = &blocks[&node_of(&request, "agent:scout")];
    let role = scout_copy.role.as_ref().unwrap();
    check(
        role.name == "subagent" && role.ink == "agentText" && role.type_chip.as_deref() == Some("Explore"),
        "a running sub-agent's identity",
    );
    let card = scout_copy.card.as_ref().unwrap();
    check(
        card.title == "scout"
            && card.identity.as_deref() == Some("Explore · Haiku · 요청")
            && card.running
            && card.status == "running"
            && !card.prose
            && !card.pending,
        "its card's words",
    );
    same(card.before.clone(), strings(&["실행 중"]), "before its time");
    check(card.started_at == Some(steps::unix_ms(&flow::date(1))) && card.ended_at.is_none(), "its start for the page's clock");
    check(
        card.copy_node_id.is_none() && card.activate_help == "블록으로 펼치기" && card.ink == "agentText" && !card.is_task,
        "it opens in place",
    );
    let done_copy = &blocks[&node_of(&request, "agent:done")];
    check(
        done_copy.role.as_ref().is_some_and(|r| r.name == "subagent")
            && done_copy.card.as_ref().is_some_and(|c| !c.running && c.status == "completed"),
        "an ended foreground sub-agent is a card too",
    );
    let task = &blocks[&node_of(&request, "agent:ci")];
    let task_role = task.role.as_ref().unwrap();
    check(
        task_role.name == "task"
            && task_role.ink == "ink2"
            && task_role.marks.len() == 1
            && task_role.marks[0].name == "background"
            && task_role.marks[0].help == "백그라운드에서 실행",
        "a command's identity",
    );
    let task_card = task.card.as_ref().unwrap();
    check(
        task_card.is_task
            && task_card.identity.as_deref() == Some("작업 · npm run build")
            && task_card.activity.as_deref() == Some("compiling")
            && task_card.ink == "ink2",
        "its card",
    );
    let line = blocks[&node_of(&request, &format!("{}main", layout::DELEGATE_SUFFIX))].line.as_ref().unwrap();
    same(line.head.as_str(), "3개 병렬", "the line's head");
    same(
        line.parts.iter().map(|p| format!("{}/{}", p.text, p.ink)).collect::<Vec<_>>(),
        strings(&["하위 에이전트 2/agentText", "작업 1/ink2"]),
        "each kind in its ink",
    );
    check(
        line.model.is_none()
            && line.progress.as_deref() == Some("2/3 실행 중")
            && line.text == "3개 병렬 · 하위 에이전트 2 · 작업 1 · 2/3 실행 중",
        "the rest of it",
    );
    check(!blocks.contains_key(&node_of(&request, layout::RETURN_SUFFIX)), "no return node: the 실행 card stands there");

    let mut stepped = r("s")
        .root(vec![
            flow::tool("e1", "git status", 2, "Read", "tool", "completed"),
            flow::tool("e2", "git push", 12, "Read", "tool", "completed"),
        ])
        .build();
    let step = |key: &str, content: &str, active: Option<&str>, status: &str, started: Option<i64>, ended: Option<i64>| FlowStep {
        key: Some(key.into()),
        content: Some(content.into()),
        active_form: active.map(str::to_string),
        status: Some(status.into()),
        started_at: started.map(flow::stamp),
        ended_at: ended.map(flow::stamp),
    };
    stepped.steps = Some(vec![
        step("text:점검", "점검", Some("점검 중"), "completed", Some(0), Some(10)),
        step("text:푸시", "푸시", Some("푸시하는 중"), "in_progress", Some(10), None),
        step("text:CI", "CI", None, "pending", None, None),
    ]);
    let stepped_runs = vec![stepped.clone()];
    let stepped_copy =
        copy::blocks(&flow::flow_layout(&stepped_runs, true, &[], true), &stepped_runs, true, &none(), &|_: &str| vec![], &ko);
    let all = stepped.steps.as_ref().unwrap();
    let first = &stepped_copy[&node_of(&stepped, &steps::node_suffix(&all[0]))];
    let first_card = first.card.as_ref().unwrap();
    check(
        first_card.title == "1. 점검"
            && first_card.status == "completed"
            && first_card.activity.as_deref() == Some("git status")
            && !first_card.prose
            && !first_card.pending,
        "a done step's card",
    );
    check(
        first.step.as_ref().is_some_and(|s| s.title == "1. 점검" && s.status == "completed" && s.entry_ids == strings(&["e1"])),
        "and what it holds",
    );
    check(!stepped_copy.contains_key(&node_of(&stepped, &steps::node_suffix(&all[2]))), "a step not begun has no card");
    let progress = &copy::progress_of(&stepped_runs, &ko)[stepped.id()];
    check(
        progress.title == "진행 상황"
            && progress.heading == "요청 1"
            && progress.count.as_deref() == Some("1/3")
            && progress.running
            && progress.hidden == 0
            && progress.more.is_none()
            && progress.request_node_id == node_of(&stepped, "request"),
        "the progress panel's head and its request",
    );
    same(
        progress.rows.iter().map(|r| format!("{}/{}", r.text, r.state)).collect::<Vec<_>>(),
        strings(&["1. 점검/done", "2. 푸시하는 중/current", "3. CI/pending"]),
        "every step and its state",
    );
    check(
        progress.rows[0].node_id == Some(node_of(&stepped, &steps::node_suffix(&all[0])))
            && progress.rows[2].node_id.is_none()
            && progress.rows[0].ended_at == Some(steps::unix_ms(&flow::date(10))),
        "where a row leads, and its times",
    );

    let shown = support::shown_runs(&runs, &ko).into_owned();
    let plain_layout =
        layout::make(&shown, "", true, &std::collections::HashSet::new(), &Options { splits_execution: true, ..Options::default() }, &ko);
    let plain_copy = copy::blocks(&plain_layout, &runs, false, &none(), &|_: &str| vec![], &ko);
    check(
        plain_copy[&node_of(&request, layout::WORK_SUFFIX)].role.as_ref().is_some_and(|r| r.name == "lead")
            && plain_copy[&node_of(&request, layout::WORK_SUFFIX)].card.is_none(),
        "without the role view the lead still wears its ink, with no card",
    );
    check(
        plain_copy[&node_of(&request, "agent:scout")].role.as_ref().is_some_and(|r| r.name == "subagent")
            && plain_copy[&node_of(&request, "agent:scout")].card.is_none(),
        "and a sub-agent its own, with no card",
    );
    check(
        !plain_copy.values().any(|b| b.line.is_some() || b.back.is_some()) && !plain_copy.contains_key(&node_of(&request, "agent:ci")),
        "no line, way back or command block",
    );

    let log_copy = copy::log_of(&runs, false, &none(), None, &ko).unwrap();
    same(log_copy.status_text.as_str(), "Sonnet 5.5 실행 중 · 하위 에이전트 1/2 실행 중 · 작업 1개 실행 중", "the status line");
    same(
        log_copy.status.iter().map(|p| p.ink.clone()).collect::<Vec<_>>(),
        strings(&["taskText", "agentText", "ink2"]),
        "each part in its role's ink",
    );
    same(
        log_copy.rows.iter().map(|r| format!("{} | {}", r.who, r.what)).collect::<Vec<_>>(),
        strings(&["scout | 시작 · Explore · Haiku · 요청"]),
        "the rows",
    );
    check(
        log_copy.rows[0].ink == "agentText"
            && log_copy.rows[0].node_id == node_of(&request, "agent:scout")
            && log_copy.rows.iter().all(|r| r.clock.len() == 8)
            && !log_copy.collapsed,
        "their inks, blocks and clocks",
    );
    let collapsed = copy::log_of(&runs, true, &none(), None, &ko).unwrap();
    check(
        collapsed.collapsed && collapsed.rows.is_empty() && collapsed.status_text == log_copy.status_text,
        "folded: the status line alone",
    );
    same(copy::log_of(&[], false, &none(), None, &ko), None, "no request, no strip");
}
