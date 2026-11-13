//! The host's `mighty` push of a pane (C# MainWindow.WebShell.Mighty.cs `WebMightyModel`, `WebTimeline`, `WebStructure` and the page
//! methods): the records name their fields as the page reads them (native/windows/web/src/mighty/types.ts, read here), and a pane projects
//! into the diagram's blocks, the layout's inputs, 기록 and the boards with the calls the diagram makes.

mod common;

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use common::{agent, ko, repo};
use indexmap::IndexMap;
use mighty_core::graph::{GraphBlockSize, MightyGraphAgent, MightyGraphRun, WORK_SUFFIX, node_id};
use mighty_core::model::{LogEntry, RunSession};
use mighty_graph::flow::copy;
use mighty_graph::images::AgentImageLocation;
use mighty_graph::result_files::ResultFile;
use mighty_graph::view::*;
use mighty_graph::{layout, support, timeline};
use regex::Regex;
use serde_json::{Value, json};

// ── types.ts ──

/// types.ts without its comments.
fn types_ts() -> String {
    let text = std::fs::read_to_string(repo().join("native/windows/web/src/mighty/types.ts")).expect("types.ts");
    let blocks = Regex::new(r"(?s)/\*.*?\*/").expect("regex").replace_all(&text, "");
    Regex::new(r"(?m)//.*$").expect("regex").replace_all(&blocks, "").into_owned()
}

/// The body of `export interface <name> { … }`.
fn interface(ts: &str, name: &str) -> String {
    let start = ts.find(&format!("export interface {name} {{")).unwrap_or_else(|| panic!("no interface {name}"));
    let open = start + ts[start..].find('{').expect("a brace");
    let mut depth = 0;
    for (at, c) in ts[open..].char_indices() {
        match c {
            '{' => depth += 1,
            '}' => {
                depth -= 1;
                if depth == 0 {
                    return ts[open + 1..open + at].to_string();
                }
            }
            _ => {}
        }
    }
    panic!("{name} never closes")
}

/// An object type's fields, each with the body of the inline object type it holds, if any.
fn fields(body: &str) -> Vec<(String, Option<String>)> {
    let name = Regex::new(r"^(\w+)\??\s*:").expect("regex");
    let mut out = vec![];
    let (mut member, mut nested, mut inner, mut depth) = (String::new(), None, String::new(), 0);
    let mut flush = |member: &mut String, nested: &mut Option<String>| {
        if let Some(found) = name.captures(member.trim()) {
            out.push((found[1].to_string(), nested.take()));
        }
        member.clear();
        *nested = None;
    };
    for c in body.chars() {
        if depth > 0 {
            match c {
                '{' => depth += 1,
                '}' => {
                    depth -= 1;
                    if depth == 0 {
                        nested = Some(std::mem::take(&mut inner));
                        continue;
                    }
                }
                _ => {}
            }
            inner.push(c);
            continue;
        }
        match c {
            '{' => depth = 1,
            ';' | '\n' => flush(&mut member, &mut nested),
            _ => member.push(c),
        }
    }
    flush(&mut member, &mut nested);
    out
}

fn names(fields: &[(String, Option<String>)]) -> Vec<String> {
    let mut list: Vec<String> = fields.iter().map(|(n, _)| n.clone()).collect();
    list.sort();
    list
}

fn nested(fields: &[(String, Option<String>)], field: &str) -> Vec<(String, Option<String>)> {
    let body = fields.iter().find(|(n, _)| n == field).and_then(|(_, b)| b.clone()).unwrap_or_else(|| panic!("{field} is no object type"));
    self::fields(&body)
}

fn keys(value: &Value) -> Vec<String> {
    let mut list: Vec<String> = value.as_object().unwrap_or_else(|| panic!("not an object: {value}")).keys().cloned().collect();
    list.sort();
    list
}

fn json_of<T: serde::Serialize>(value: &T) -> Value {
    serde_json::to_value(value).expect("json")
}

fn some(text: &str) -> Option<String> {
    Some(text.into())
}

fn full_block() -> WebMightyBlock {
    WebMightyBlock {
        kind: "result".into(),
        title: "t".into(),
        state: "s".into(),
        capsule: some("c"),
        help: "h".into(),
        indicator: "none".into(),
        outline: "none".into(),
        status: "completed".into(),
        tone: "done".into(),
        files_run_id: some("r"),
        record_id: some("k"),
        from_record: false,
        provider: some("claude"),
        background: Some(true),
        output_tail: Some(vec!["line".into()]),
        notice_reply: some("reply"),
        notice: Some(true),
        awaiting: Some(WebMightyAwaiting {
            tasks: vec![WebMightyAwaitedTask { title: "t".into(), status: "running".into(), word: "w".into() }],
            progress: "p".into(),
        }),
        so_far: Some(true),
        flow: Some(json!({})),
    }
}

fn full_inputs() -> WebMightyInputs {
    WebMightyInputs {
        plan_run_id: some("r"),
        plan_records: vec![WebPlanRecord { run_id: "r".into(), record_id: "k".into() }],
        galleries: vec![WebGallery { run_id: "r".into(), step: "request".into(), count: 1 }],
        executions: vec![WebExecution { run_id: "r".into(), key: "k".into() }],
        result_files_run_id: some("r"),
        files: IndexMap::from([("r".to_string(), vec![WebMightyFile { path: "a.rs".into(), line: Some(3) }])]),
        older: 0,
        retained_start: 0,
        history: Some(WebMightyHistory { text: "t".into(), acts: true, phase: "idle".into() }),
        summary: "s".into(),
        summary_help: "h".into(),
        execution_cards: IndexMap::new(),
        animations: true,
        header: "Mighty".into(),
        fan_out: Some(true),
        log: Some(json!({})),
        progress: Some(json!({})),
    }
}

fn full_row() -> WebTimelineRow {
    WebTimelineRow {
        node_id: "n".into(),
        kind: "agent".into(),
        kind_label: "k".into(),
        title: "t".into(),
        status: "running".into(),
        status_word: "w".into(),
        tone: "run".into(),
        ring: true,
        rail: some("run"),
        latest: some("l"),
        duration: some("1초"),
        capsule: some("c"),
        input: some("i"),
        agent_index: Some(0),
        ink: some("agentText"),
    }
}

fn full_group() -> WebTimelineGroup {
    WebTimelineGroup {
        run_id: "r".into(),
        ordinal: 1,
        title: "t".into(),
        status: "completed".into(),
        status_word: "w".into(),
        tone: "done".into(),
        input: "i".into(),
        tally: "1/1".into(),
        open: true,
        older: false,
        rows: vec![full_row()],
        result_node_id: some("n"),
        result_tone: some("done"),
        result: some("r"),
        result_folds: false,
        result_caption: some("c"),
        notice_run: Some(true),
        result_title: some("t"),
    }
}

fn full_card() -> WebStructureCard {
    WebStructureCard {
        id: "c".into(),
        kind: "agent".into(),
        run_id: "r".into(),
        node_id: "n".into(),
        title: "t".into(),
        status: "running".into(),
        status_word: "w".into(),
        tone: "run".into(),
        parent: some("p"),
        text: some("t"),
        message: some("m"),
        answer: some("a"),
        activity: vec![WebStructureActivity { kind: "read".into(), state: "completed".into(), text: "a.rs".into() }],
        tally: some("t"),
        finished: false,
        result_line: some("l"),
        node_kind: some("agent"),
        meta: some("m"),
        depth: 1,
        prompt: some("p"),
        execution_node_id: some("e"),
        result_node_id: some("r"),
        tint: some("run"),
        background: Some(true),
        output_tail: Some(vec!["l".into()]),
        notice_reply: some("n"),
        result_title: some("t"),
        result_awaiting: Some(true),
        result_waiting: Some(true),
    }
}

fn full_board() -> WebStructureBoard {
    WebStructureBoard {
        id: "b".into(),
        kind: "pipeline".into(),
        ordinal: 1,
        headline: some("h"),
        status: "running".into(),
        status_word: "w".into(),
        tone: "run".into(),
        run_ids: vec!["r".into()],
        columns: vec![WebStructureColumn {
            id: "c".into(),
            title: "t".into(),
            step: "current".into(),
            step_word: "w".into(),
            count: 1,
            cards: vec![full_card()],
            tone: some("run"),
        }],
        style_name: some("s"),
        quiet: Some(true),
    }
}

fn empty_projection(blocks: &[(&str, WebMightyBlock)]) -> MightyProjection {
    MightyProjection {
        blocks: blocks.iter().map(|(id, b)| (id.to_string(), b.clone())).collect(),
        inputs: full_inputs(),
        older_key: "0:".into(),
        older_runs: vec![],
        timeline: Some(vec![full_group()]),
        structure: Some(vec![full_board()]),
    }
}

#[test]
fn the_records_name_their_fields_as_the_page_reads_them() {
    let ts = types_ts();
    let of = |name: &str| fields(&interface(&ts, name));
    let same = |interface: &str, ts: Vec<String>, rust: Vec<String>| assert_eq!(rust, ts, "{interface}: the Rust record and types.ts");
    same("MightyBlockCopy", names(&of("MightyBlockCopy")), keys(&json_of(&full_block())));
    let awaiting = of("MightyAwaiting");
    same("MightyAwaiting", names(&awaiting), keys(&json_of(&full_block().awaiting)));
    same("MightyAwaiting.tasks", names(&nested(&awaiting, "tasks")), keys(&json_of(&full_block().awaiting.unwrap().tasks[0])));
    same("MightyFile", names(&of("MightyFile")), keys(&json_of(&WebMightyFile { path: "a".into(), line: Some(1) })));
    let inputs = of("MightyInputs");
    let written = json_of(&full_inputs());
    same("MightyInputs", names(&inputs), keys(&written));
    for field in ["planRecords", "galleries", "executions"] {
        same(&format!("MightyInputs.{field}"), names(&nested(&inputs, field)), keys(&written[field][0]));
    }
    same("MightyInputs.history", names(&nested(&inputs, "history")), keys(&written["history"]));
    same("TimelineRow", names(&of("TimelineRow")), keys(&json_of(&full_row())));
    same("TimelineGroup", names(&of("TimelineGroup")), keys(&json_of(&full_group())));
    same("StructureCard", names(&of("StructureCard")), keys(&json_of(&full_card())));
    same("StructureActivity", names(&of("StructureActivity")), keys(&json_of(&full_card().activity[0])));
    let board = json_of(&full_board());
    same("StructureColumn", names(&of("StructureColumn")), keys(&board["columns"][0]));
    same("StructureBoard", names(&of("StructureBoard")), keys(&board));

    // The message: every part it can carry, over a first push and a change that removes a block.
    let mut sent = MightySent::new();
    let first = sent.message("p", &empty_projection(&[("a", full_block()), ("b", full_block())])).expect("the whole model");
    let mut changed = full_block();
    changed.title = "changed".into();
    let mut next = empty_projection(&[("a", changed)]);
    next.older_key = "1:r/completed/0".into();
    let second = sent.message("p", &next).expect("a change");
    let mut message: Vec<String> = keys(&first).into_iter().chain(keys(&second)).collect::<HashSet<_>>().into_iter().collect();
    message.sort();
    same("MightyMessage", names(&of("MightyMessage")), message);

    // The role view's copy (FlowCopy) as the page reads it; the return node's `back` is the host's own, which the page does not read.
    let role = copy::Role {
        name: "subagent".into(),
        ink: "agentText".into(),
        kind: some("Explore"),
        type_chip: some("Explore"),
        effort: some("High"),
        effort_help: some("h"),
        marks: vec![copy::Mark { name: "background".into(), help: "h".into() }],
        summary: "s".into(),
        model_label: some("Haiku 5.5"),
    };
    let card = copy::Card {
        title: "t".into(),
        identity: some("i"),
        activity: some("a"),
        status: "running".into(),
        state_word: "w".into(),
        before: vec![],
        after: vec![],
        started_at: Some(1),
        ended_at: Some(2),
        running: true,
        nested: 1,
        nested_help: some("n"),
        marks: vec![],
        ink: "agentText".into(),
        summary: "s".into(),
        is_task: false,
        copy_node_id: some("c"),
        activate_help: "h".into(),
        prose: true,
        pending: true,
        step: some("2. 노트"),
        step_help: some("h"),
    };
    let line = copy::Line {
        head: "h".into(),
        parts: vec![copy::Part { text: "t".into(), ink: "i".into() }],
        model: some("m"),
        progress: some("p"),
        text: "t".into(),
    };
    let step = copy::StepCopy { title: "t".into(), state_word: "w".into(), status: "running".into(), entry_ids: vec!["e".into()] };
    let block = copy::Block {
        role: Some(role.clone()),
        card: Some(card.clone()),
        line: Some(line.clone()),
        back: Some(copy::Back { text: "t".into(), waiting: true }),
        step: Some(step),
    }
    .to_json();
    let flow = of("FlowBlockCopy");
    let mut read = names(&flow);
    read.push("back".into());
    read.sort();
    same("FlowBlockCopy (and the host's back)", read, keys(&block));
    same("FlowRoleCopy", names(&of("FlowRoleCopy")), keys(&role.to_json()));
    same("FlowMarkCopy", names(&of("FlowMarkCopy")), keys(&role.marks[0].to_json()));
    same("FlowCardCopy", names(&of("FlowCardCopy")), keys(&card.to_json()));
    same("FlowLineCopy", names(&of("FlowLineCopy")), keys(&line.to_json()));
    same("FlowPartCopy", names(&of("FlowPartCopy")), keys(&line.parts[0].to_json()));
    same("FlowStepCopy", names(&of("FlowStepCopy")), keys(&block["step"]));
    let progress = copy::ProgressCopy {
        title: "t".into(),
        heading: "h".into(),
        request: "r".into(),
        state_word: "w".into(),
        status: "running".into(),
        count: some("1/2"),
        rows: vec![copy::ProgressRow {
            index: 0,
            node_id: some("n"),
            text: "t".into(),
            state: "current".into(),
            started_at: Some(1),
            ended_at: Some(2),
        }],
        hidden: 0,
        more: some("m"),
        running: true,
        request_node_id: "n".into(),
        request_help: "h".into(),
        background: Some(vec![copy::ProgressBackgroundRow {
            id: "b".into(),
            text: "t".into(),
            agent: true,
            state: "current".into(),
            started_at: Some(1),
            ended_at: Some(2),
        }]),
        background_hidden: 0,
    }
    .to_json();
    same("FlowProgressCopy", names(&of("FlowProgressCopy")), keys(&progress));
    same("FlowProgressRowCopy", names(&of("FlowProgressRowCopy")), keys(&progress["rows"][0]));
    same("FlowProgressBackgroundCopy", names(&of("FlowProgressBackgroundCopy")), keys(&progress["background"][0]));
    let log = copy::Log {
        status: vec![copy::Part { text: "t".into(), ink: "i".into() }],
        status_text: "t".into(),
        rows: vec![copy::LogRow {
            id: "i".into(),
            clock: "12:00".into(),
            who: "w".into(),
            ink: "i".into(),
            what: "w".into(),
            node_id: "n".into(),
        }],
        collapsed: false,
    }
    .to_json();
    let log_fields = of("FlowLogCopy");
    same("FlowLogCopy", names(&log_fields), keys(&log));
    same("FlowLogCopy.rows", names(&nested(&log_fields, "rows")), keys(&log["rows"][0]));
}

// ── the projection ──

struct Dashboard;

impl ExecutionDashboard for Dashboard {
    fn block_status(&self, key: &str) -> String {
        if key == "live" { "running".into() } else { "starting".into() }
    }
    fn card(&self, key: &str, list_open: bool) -> Option<Value> {
        Some(json!({"key": key, "listOpen": list_open}))
    }
}

fn refused(_: &str, _: Option<&str>) -> AgentImageLocation {
    AgentImageLocation::Refused
}

struct World {
    tails: HashMap<String, Vec<String>>,
    definitions: HashMap<String, mighty_graph::flow::roles::AgentDefinition>,
    executions: Vec<(String, String)>,
}

impl World {
    fn new() -> World {
        World { tails: HashMap::new(), definitions: HashMap::new(), executions: vec![] }
    }

    fn sources<'a>(&'a self, older: &'a [MightyGraphRun]) -> Sources<'a> {
        Sources {
            older,
            history: Some(WebMightyHistory { text: "이전 요청 불러오기".into(), acts: true, phase: "idle".into() }),
            pending_plan: false,
            executions: &self.executions,
            dashboard: &Dashboard,
            root: None,
            images: &refused,
            tails: &self.tails,
            definitions: &self.definitions,
            catalog: None,
            style: None,
            animations: true,
            zone: None,
        }
    }
}

fn run(id: &str, status: &str, final_output: Option<&str>, agents: Vec<MightyGraphAgent>) -> MightyGraphRun {
    let mut run = MightyGraphRun {
        id: Some(id.into()),
        input: Some(format!("do {id}")),
        status: Some(status.into()),
        root_entries: Some(vec![]),
        result_entries: Some(vec![]),
        agents: Some(agents),
        final_output: final_output.map(str::to_string),
        provider: Some("claude".into()),
        ..MightyGraphRun::default()
    };
    support::refresh_result(&mut run);
    run
}

fn pane(provider: &str, runs: Vec<MightyGraphRun>, status: &str) -> RunSession {
    RunSession {
        id: Some("pane".into()),
        kind: Some("claude".into()),
        provider: Some(provider.into()),
        status: Some(status.into()),
        resume_id: Some("session-1".into()),
        graph_runs: Some(runs),
        ..RunSession::default()
    }
}

fn task(id: &str, status: &str) -> MightyGraphAgent {
    MightyGraphAgent {
        kind: Some("task".into()),
        background: Some(true),
        input: Some("npm run build".into()),
        title: Some("Build".into()),
        ..agent(id, None, status, Some("task"))
    }
}

#[test]
fn a_pane_projects_as_the_diagram_draws_it() {
    let ko = ko();
    let mut world = World::new();
    let older = vec![run("old", "completed", Some("earlier"), vec![])];
    let request = run("r", "completed", Some("Started."), vec![agent("fg", None, "completed", None), task("t", "running")]);
    world.tails.insert("t".into(), (1..=8).map(|i| i.to_string()).collect());
    world.executions = vec![("r".into(), "live".into())];
    let pane = pane("claude", vec![request], "idle");
    let mut state = MightyPane::new();
    let made = project(&pane, &mut state, &world.sources(&older), &ko);

    // Every block the diagram lays out has its copy, in its order, and the role view's own nodes after them.
    let laid = layout::make(
        &[older.clone(), pane.graph_runs().to_vec()].concat(),
        "",
        false,
        &HashSet::new(),
        &layout::Options {
            retained_start: 1,
            history: true,
            splits_execution: true,
            executions: world.executions.clone(),
            fan_out: Some(layout::FanOut::open_all()),
            ..layout::Options::default()
        },
        &ko,
    );
    for node in laid.nodes.iter().filter(|n| !matches!(n.kind.as_str(), "history" | "draft")) {
        assert!(made.blocks.contains_key(&node.id), "{} ({}) has a copy", node.id, node.kind);
    }
    let request_block = &made.blocks[&node_id("r", "request")];
    assert_eq!(request_block.title, "요청 2 · Claude", "the request's title counts the older one");
    assert_eq!(request_block.provider.as_deref(), Some("claude"), "its provider's mark");
    assert!(
        !request_block.from_record && made.blocks[&node_id("old", "request")].from_record,
        "only the older request is read from the record"
    );
    assert!(!made.blocks[&node_id("old", "result")].from_record, "a result never says so");
    // The role view draws the request's background command as a card in its row, with its last lines and its kind.
    let command = &made.blocks[&node_id("r", "agent:t")];
    assert_eq!(command.background, Some(true), "a background block says so");
    assert_eq!(command.output_tail, Some((3..=8).map(|i| i.to_string()).collect()), "its last six lines");
    assert!(command.flow.as_ref().is_some_and(|f| f.get("card").is_some()), "its card");
    let result = &made.blocks[&node_id("r", "result")];
    assert!(
        result.title == "알림 대기 중" && result.status == "waiting" && result.tone == "wait" && result.awaiting.is_some(),
        "the request awaiting its command's notice is the 알림 대기 block: {result:?}"
    );
    let awaiting = result.awaiting.as_ref().expect("awaiting");
    assert!(
        awaiting.tasks.len() == 1 && awaiting.tasks[0].word == "끝나면 알림" && awaiting.progress == "알림 0/1",
        "its rows: {awaiting:?}"
    );
    // The background execution's block reads its dashboard; its card comes with it.
    let execution = made.blocks.values().find(|b| b.kind == layout::EXECUTION_KIND).expect("the execution's block");
    assert!(execution.status == "running" && execution.indicator == "animating", "the execution's block: {execution:?}");
    let execution_id = made.blocks.iter().find(|(_, b)| b.kind == layout::EXECUTION_KIND).map(|(id, _)| id.clone()).expect("an id");
    assert_eq!(made.inputs.execution_cards[&execution_id], json!({"key": "live", "listOpen": false}), "its card, its list closed");

    // The layout's inputs and the toolbar's words.
    let inputs = &made.inputs;
    assert!(inputs.older == 1 && inputs.retained_start == 1, "one older request");
    assert!(inputs.summary.starts_with("요청 1") && inputs.summary.contains(" · "), "the summary of the retained ones: {}", inputs.summary);
    assert!(inputs.summary.ends_with(&ko.get_with("graph.history.headerLoaded", &[("count", "1")])), "and the older ones loaded");
    assert_eq!(inputs.history.as_ref().map(|h| h.acts), Some(true), "the history block, from the record reader");
    assert!(inputs.fan_out == Some(true) && inputs.log.is_some(), "a Claude pane is the role view with its log");
    assert!(inputs.progress.as_ref().is_some_and(|p| p.get("r").is_some()), "the progress panel of each request");
    assert_eq!(inputs.header, ko.get("graph.view.mighty"), "no style on");
    assert!(made.timeline.is_none() && made.structure.is_none(), "the diagram makes neither 기록 nor boards");
    assert_eq!(made.older_key, older_key(&older), "the older requests' key");
    assert_eq!(state.open_dashboard("live"), Some(&("r".to_string(), "live".to_string())), "the dashboard it drew opens");
    assert_eq!(state.open_dashboard("other"), None, "one it did not draw does not");

    // A pane of another agent is no role view, and a pane with no session to read has no history block.
    let mut codex = pane.clone();
    codex.provider = Some("codex".into());
    let plain = project(&codex, &mut MightyPane::new(), &world.sources(&older), &ko);
    assert!(plain.inputs.fan_out.is_none() && plain.inputs.log.is_none(), "a Codex pane draws the plain diagram");
    assert!(plain.blocks[&node_id("old", WORK_SUFFIX)].from_record, "the older request's 실행 block says where it was read from");
    assert!(!plain.blocks.contains_key(&node_id("r", "agent:t")), "where a request draws no background block");
    assert_eq!(plain.blocks[&node_id("r", "request")].title, "요청 2 · Codex", "named after its agent");
    let mut opencode = pane.clone();
    opencode.provider = Some("opencode".into());
    assert!(project(&opencode, &mut MightyPane::new(), &world.sources(&[]), &ko).inputs.history.is_none(), "no record to read");
}

#[test]
fn the_timeline_and_the_structure_come_in_their_presentations() {
    let ko = ko();
    let world = World::new();
    let runs = vec![
        run("a", "completed", Some("done"), vec![agent("child", None, "completed", None)]),
        run("b", "running", None, vec![agent("live", None, "running", None)]),
    ];
    // A Codex pane draws the plain diagram, whose 실행 blocks every 기록 row can name (the role view folds a finished one away).
    let mut timeline_pane = pane("codex", runs.clone(), "running");
    timeline_pane.graph_view_mode = Some("timeline".into());
    let mut state = MightyPane::new();
    let made = project(&timeline_pane, &mut state, &world.sources(&[]), &ko);
    let groups = made.timeline.as_ref().expect("기록 in the timeline presentation");
    assert!(made.structure.is_none(), "no boards");
    assert_eq!(groups.iter().map(|g| g.run_id.as_str()).collect::<Vec<_>>(), ["a", "b"], "one group a request");
    assert!(!groups[0].open && groups[1].open, "the newest is open");
    assert_eq!(groups[0].result.as_deref(), Some("done"), "the answer");
    assert_eq!(groups[0].result_title.as_deref(), Some("최종 결과"), "its head's title");
    assert_eq!(groups[1].title, "요청 2 · Codex", "titled as the diagram titles it");
    // Every row names a block of the diagram the page draws.
    for row in groups.iter().flat_map(|g| &g.rows) {
        assert!(made.blocks.contains_key(&row.node_id), "{} is a block", row.node_id);
    }
    let work = &groups[1].rows[1];
    assert!(
        work.kind == timeline::EXECUTION_KIND && work.ink.as_deref() == Some("taskText") && work.kind_label.starts_with("실행"),
        "the 실행 row: {work:?}"
    );
    assert_eq!(groups[1].rows[0].input.as_deref(), Some("do b"), "the request row holds what was asked");
    let child = &groups[1].rows[2];
    assert!(child.ring && child.status_word == "실행 중", "a running block rings");
    // A flip opens or folds a group against its default.
    state.timeline_flip("a");
    assert!(project(&timeline_pane, &mut state, &world.sources(&[]), &ko).timeline.expect("기록")[0].open, "flipped open");
    state.timeline_flip("a");
    assert!(state.timeline_flipped.is_empty(), "flipped back");

    let mut structure_pane = timeline_pane.clone();
    structure_pane.graph_view_mode = Some("structure".into());
    let made = project(&structure_pane, &mut state, &world.sources(&[]), &ko);
    let boards = made.structure.as_ref().expect("boards in the structure presentation");
    assert!(made.timeline.is_none(), "no 기록");
    assert_eq!(boards.len(), 2, "a free pane has a board a request");
    let board = &boards[1];
    assert!(board.kind == "request" && board.style_name.is_none() && board.quiet.is_none(), "a request's board: {board:?}");
    assert_eq!(board.columns.iter().map(|c| c.id.as_str()).collect::<Vec<_>>(), ["request", "execution", "result"], "its steps");
    assert_eq!(board.columns[0].title, ko.get("graph.structure.column.request"), "named by the view");
    let request = &board.columns[0].cards[0];
    assert!(
        request.title == "요청 2 · Codex" && request.result_node_id.as_deref() == Some(node_id("b", "result").as_str()),
        "its request card"
    );
    let finished = &boards[0].columns[2].cards;
    assert!(
        finished.iter().any(|c| c.result_title.as_deref() == Some("최종 결과") && c.result_awaiting.is_none()),
        "the finished request's result card: {finished:?}"
    );

    // A guided style: the toolbar names it and its phase, a request its prompt's prefix, and its pipeline board carries its name.
    let flow = common::style("ouroboros");
    let phase = common::phase(&flow, "interview").clone();
    let prefix = |input: &str| input.starts_with("ooo ").then(|| "인터뷰".to_string());
    let mut styled_world = World::new();
    styled_world.executions = vec![];
    let mut sources = styled_world.sources(&[]);
    sources.style = Some(StyleSource {
        name: "Ouroboros",
        phase_title: Some("인터뷰"),
        structure: &flow,
        phase: Some(&phase),
        request_prefix: &prefix,
    });
    let mut styled = structure_pane.clone();
    styled.graph_runs = Some(vec![
        run("s1", "completed", Some("asked"), vec![]),
        MightyGraphRun { input: Some("ooo interview 할 일 앱".into()), ..run("s2", "running", None, vec![]) },
    ]);
    let made = project(&styled, &mut MightyPane::new(), &sources, &ko);
    assert_eq!(made.inputs.header, format!("{} · Ouroboros · 인터뷰", ko.get("graph.view.mighty")), "the header");
    assert_eq!(made.blocks[&node_id("s2", "request")].title, "인터뷰 · 요청 2 · Codex", "the prompt's prefix");
    let boards = made.structure.expect("boards");
    assert!(
        boards.iter().all(|b| b.kind == "pipeline" && b.style_name.as_deref() == Some("Ouroboros")),
        "a styled pane's pipelines: {:?}",
        boards.iter().map(|b| (&b.kind, &b.style_name)).collect::<Vec<_>>()
    );
    assert!(
        boards.iter().flat_map(|b| &b.columns).any(|c| c.id == "interview" && c.title == phase.title),
        "the phases' columns, by the style's titles"
    );
}

#[test]
fn the_page_methods_change_the_pane_and_its_state() {
    let ko = ko();
    let older = vec![run("old", "completed", Some("earlier"), vec![])];
    let pane = pane("claude", vec![run("r", "completed", Some("done"), vec![])], "idle");
    let mut state = MightyPane::new();
    let size = GraphBlockSize { width: 500.0, height: 400.0 };

    // mightySetPresentation: only a word the build knows.
    assert_eq!(MightyPane::set_presentation(&pane, "timeline").graph_view_mode.as_deref(), Some("timeline"), "the timeline");
    assert_eq!(MightyPane::set_presentation(&pane, "odd"), pane, "an unknown word changes nothing");

    // mightySaveSize: the newest result and the plan keep the pane's one size, a block its own, a block of the record only here.
    let result = state.save_size(&pane, &node_id("r", "result"), "result", Some(size), &older).expect("a pane");
    assert_eq!(result.graph_result_size, size.normalized(), "the result's size");
    let plan = state.save_size(&pane, "x", "plan", Some(size), &older).expect("a pane");
    assert_eq!(plan.graph_plan_size, size.normalized(), "the plan's size");
    let own = node_id("r", "agent:a");
    let saved = state.save_size(&pane, &own, "block", Some(size), &older).expect("a pane");
    assert!(saved.graph_block_sizes.as_ref().is_some_and(|s| s.contains_key(&own)), "the pane's own block");
    let read = node_id("old", WORK_SUFFIX);
    assert!(state.save_size(&pane, &read, "block", Some(size), &older).is_none(), "a block of the record changes no pane");
    assert!(state.record_sizes.contains_key(&read), "it is kept here");
    let cleared = state.save_size(&saved, &own, "block", None, &older).expect("a pane");
    assert!(cleared.graph_block_sizes.as_ref().is_none_or(|s| !s.contains_key(&own)), "a size put back");

    // mightyExpand: the size goes, the block opens and folds.
    let opened = state.expand(&saved, &own, true);
    assert!(
        state.expanded.contains(&own) && opened.graph_block_sizes.as_ref().is_none_or(|s| !s.contains_key(&own)),
        "opened, its size gone"
    );
    state.expand(&opened, &read, true);
    assert!(!state.record_sizes.contains_key(&read), "a record block's size goes too");
    state.expand(&opened, &own, false);
    assert!(!state.expanded.contains(&own), "folded");

    // mightyResultFiles: a result that names files toggles its list; none closes it.
    let mut files_pane = MightyPane::new();
    let mut world = World::new();
    world.executions = vec![];
    let mut sources = world.sources(&[]);
    sources.root = Some("C:/work");
    project(&pane, &mut files_pane, &sources, &ko);
    let lookups = files_pane.take_result_file_lookups();
    assert_eq!(lookups.iter().map(|l| l.run.id()).collect::<Vec<_>>(), ["r"], "the finished result is handed to be looked through");
    assert!(files_pane.take_result_file_lookups().is_empty(), "once");
    project(&pane, &mut files_pane, &sources, &ko);
    assert!(files_pane.take_result_file_lookups().is_empty(), "not again while it is looked through");
    files_pane.found_result_files("r", lookups[0].key, vec![ResultFile { path: "src/a.rs".into(), line: Some(4) }]);
    let made = project(&pane, &mut files_pane, &sources, &ko);
    assert_eq!(made.inputs.files["r"][0].path, "src/a.rs", "its files");
    assert_eq!(made.inputs.result_files_run_id.as_deref(), Some("r"), "the newest result that names files opens its list by itself");
    files_pane.result_files(Some("r"));
    assert_eq!(files_pane.result_files.selected_run_id(), None, "toggled shut");
    files_pane.result_files(Some("r"));
    assert_eq!(files_pane.result_files.selected_run_id(), Some("r"), "and open");
    files_pane.result_files(None);
    assert_eq!(files_pane.result_files.selected_run_id(), None, "closed");
    files_pane.result_files(Some("other"));
    assert_eq!(files_pane.result_files.selected_run_id(), None, "a result naming nothing has no list");
    let remote = project(&pane, &mut MightyPane::new(), &world.sources(&[]), &ko);
    assert!(remote.inputs.files.is_empty(), "a remote pane's files are its host's");

    // mightyLogCollapsed.
    assert_eq!(MightyPane::log_collapsed(&pane, true).flow_log_collapsed, Some(true), "folded");
    assert_eq!(MightyPane::log_collapsed(&pane, false).flow_log_collapsed, None, "open writes nothing");

    // mightyLoadOlder: an agent pane whose records are read, with a folder here.
    let asked = MightyPane::load_older(&pane, Some("C:/work")).expect("a read");
    assert!(asked.provider == "claude" && asked.resume_id == "session-1" && asked.first_retained.as_deref() == Some("r"), "{asked:?}");
    assert!(MightyPane::load_older(&pane, None).is_none(), "a remote pane reads no record here");
    let mut other = pane.clone();
    other.provider = Some("pi".into());
    assert!(MightyPane::load_older(&other, Some("C:/work")).is_none(), "pi keeps no record the app reads");

    // mightyImages: a step's pictures in a run the pane or its record holds.
    let image = LogEntry {
        images: Some(vec![Some(mighty_core::model::AgentImageRef {
            hash: Some("h1".into()),
            media_type: Some("image/png".into()),
            width: 1,
            height: 1,
            bytes: 1,
            source: None,
            path: None,
        })]),
        ..common::entry("e", "assistant", "a picture", common::STAMP)
    };
    let mut pictured = pane.clone();
    pictured.graph_runs.as_mut().expect("runs")[0].root_entries = Some(vec![image]);
    let page = MightyPane::images(&pictured, &older, "r", "request", false, None, &refused).expect("its pictures");
    assert!(page.count == 1 && page.shown.len() == 1, "{page:?}");
    assert!(MightyPane::images(&pictured, &older, "old", "request", false, None, &refused).is_some_and(|p| p.count == 0), "a record's run");
    assert!(MightyPane::images(&pictured, &older, "gone", "request", false, None, &refused).is_none(), "a run neither holds");

    // mightyVisible.
    state.set_shown(true);
    assert!(state.shown, "shown");
}

#[test]
fn a_shown_pane_is_projected_when_it_changed_and_every_eighth_tick() {
    let mut pushes = MightyPushes::default();
    let first = Arc::new(pane("claude", vec![], "idle"));
    let sessions = vec![first.clone()];
    assert!(pushes.due(&sessions).is_empty(), "no pane shown");
    pushes.visible("pane", true);
    assert_eq!(pushes.due(&sessions).len(), 1, "a pane just shown is projected");
    assert!(pushes.due(&sessions).is_empty(), "the same session is not");
    pushes.dirty("pane");
    assert_eq!(pushes.due(&sessions).len(), 1, "a page method asks for it");
    let changed = vec![Arc::new((*first).clone())];
    assert_eq!(pushes.due(&changed).len(), 1, "a new session is");
    let mut ticks = 0;
    while pushes.due(&changed).is_empty() {
        ticks += 1;
        assert!(ticks < 8, "never more than seven quiet ticks");
    }
    assert!(pushes.shown().any(|id| id == "pane"), "still shown");
    assert!(pushes.due(&[]).is_empty() && pushes.shown().next().is_none(), "a pane that left the state stops being shown");

    // The message the projection makes goes out once.
    let ko = ko();
    let world = World::new();
    let shown = pane("claude", vec![run("r", "completed", Some("done"), vec![])], "idle");
    pushes.visible("pane", true);
    let model = project(&shown, &mut MightyPane::new(), &world.sources(&[]), &ko);
    let message = pushes.message("pane", &model).expect("the whole model");
    assert!(message["blocks"].as_object().is_some_and(|b| b.contains_key(&node_id("r", "result"))), "the blocks");
    assert!(!pushes.last_set("pane").is_empty(), "the blocks it set");
    assert!(pushes.message("pane", &model).is_none(), "nothing changed");
}
