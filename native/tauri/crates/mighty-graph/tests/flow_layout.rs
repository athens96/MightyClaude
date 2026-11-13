//! The role view's diagram (C# FlowLayoutVerification, MightyCode.Core.Tests/FlowLayoutVerification.cs): the delegate line and the return
//! node, the follow camera, every sub-agent and background command in the fan-out row, and background commands in the row.

mod view_support;

use std::collections::{HashMap, HashSet};

use mighty_core::graph::{GraphBlockSize, GraphTokenUsage, MightyGraphAgent, MightyGraphRun};
use mighty_core::locale::Locale;
use mighty_graph::camera::{self, Anchor, Point, Size, StrandedWatch};
use mighty_graph::flow::card;
use mighty_graph::flow::lines::{self, Part};
use mighty_graph::flow::log;
use mighty_graph::flow::roles::{self, AgentDefinition, FlowInk, FlowMark, FlowRole};
use mighty_graph::flow::steps;
use mighty_graph::follow::{self, Watch};
use mighty_graph::layout::{self, FanOut, Layout, Options};
use mighty_graph::{node, notice, support};
use view_support::flow::{self, a, find, must, node_of, overlap, r};
use view_support::*;

const LINE_DROP: f64 = layout::DELEGATE_GAP + lines::DELEGATE_HEIGHT + layout::DELEGATE_ROW_GAP;

/// `Plain(id, agents, status, notice)`: no responses, its result's answer once it completed.
fn plain(id: &str, agents: Vec<MightyGraphAgent>, status: &str, notice: bool) -> MightyGraphRun {
    let mut run = with_agents(run(id, &format!("요청 {id}"), status), agents);
    run.final_output = (status == "completed").then(|| format!("결과 {id}"));
    run.notice_run = notice.then_some(true);
    run
}

#[derive(Default, Clone)]
struct Sub<'a> {
    parent: Option<&'a str>,
    status: Option<&'a str>,
    background: bool,
    kind: Option<&'a str>,
    source: Option<&'a str>,
    requested: Option<&'a str>,
    resolved: Option<&'a str>,
    answered: Option<&'a str>,
    kind_type: Option<&'a str>,
}

/// `Sub(id, …)`: a sub-agent as FlowLinesTests builds it.
fn sub(id: &str, s: Sub) -> MightyGraphAgent {
    let mut b = a(id).status(s.status.unwrap_or("running")).identity(identity(s.kind_type, s.requested, s.resolved, None));
    if let Some(p) = s.parent {
        b = b.parent(p);
    }
    if s.background {
        b = b.background();
    }
    if let Some(k) = s.kind {
        b = b.kind(k);
    }
    if let Some(src) = s.source {
        b = b.source(src);
    }
    if let Some(model) = s.answered {
        b = b.records(vec![flow::answered(&format!("{id}-m"), model)]);
    }
    b.build()
}

fn running_sub(id: &str) -> MightyGraphAgent {
    sub(id, Sub::default())
}

fn answered_sub(id: &str, model: &str) -> MightyGraphAgent {
    sub(id, Sub { answered: Some(model), ..Sub::default() })
}

/// `Fan(id, parent, status = "completed", background, kind, source)`.
fn fan(id: &str, parent: Option<&str>, status: &str, background: bool, kind: Option<&str>, source: Option<&str>) -> MightyGraphAgent {
    sub(id, Sub { parent, status: Some(status), background, kind, source, ..Sub::default() })
}

fn plain_layout(runs: &[MightyGraphRun], running: bool) -> Layout {
    let shown = support::shown_runs(runs, &ko());
    layout::make(&shown, "", running, &HashSet::new(), &Options { splits_execution: true, ..Options::default() }, &ko())
}

fn fan_layout(runs: &[MightyGraphRun], running: bool) -> Layout {
    layout::make(
        runs,
        "",
        running,
        &HashSet::new(),
        &Options { splits_execution: true, fan_out: Some(FanOut::default()), ..Options::default() },
        &ko(),
    )
}

fn agent_id(run: &MightyGraphRun, id: &str) -> String {
    node_of(run, &format!("agent:{id}"))
}

fn line_id(run: &MightyGraphRun, key: &str) -> String {
    node_of(run, &format!("{}{key}", layout::DELEGATE_SUFFIX))
}

fn of(children: &[MightyGraphAgent], definitions: &HashMap<String, AgentDefinition>, locale: &Locale) -> Option<lines::Line> {
    let refs: Vec<&MightyGraphAgent> = children.iter().collect();
    lines::line_of(&refs, definitions, locale)
}

fn none() -> HashMap<String, AgentDefinition> {
    HashMap::new()
}

/// "flow lines: the delegate line says how many, on which model and how many run; the way back what is true now"
/// (TheDelegateLineSaysHowManyOnWhichModelAndHowManyRun).
#[test]
fn the_delegate_line_says_how_many_on_which_model_and_how_many_run() {
    let ko = ko();
    let mut three = vec![
        answered_sub("a", "claude-haiku-5-5"),
        answered_sub("b", "claude-haiku-5-5"),
        sub("c", Sub { status: Some("completed"), answered: Some("claude-haiku-5-5"), ..Sub::default() }),
    ];
    let line = of(&three, &none(), &ko).unwrap();
    check(line.count == 3 && line.running == 2, "three, two running");
    same(line.head.as_str(), "하위 에이전트 3개에 위임", "the head");
    same(line.model.as_deref(), Some("Haiku 5.5"), "the shared model");
    same(line.progress.as_deref(), Some("2/3 실행 중"), "how many run");
    same(line.text().as_str(), "하위 에이전트 3개에 위임 · Haiku 5.5 · 2/3 실행 중", "the whole line");
    for agent in &mut three {
        agent.status = Some("completed".into());
    }
    let ended = of(&three, &none(), &ko).unwrap();
    same(ended.progress.clone(), None, "all ended");
    same(ended.text().as_str(), "하위 에이전트 3개에 위임 · Haiku 5.5", "without the count");
    same(
        of(&[sub("solo", Sub { status: Some("completed"), ..Sub::default() })], &none(), &ko).unwrap().text().as_str(),
        "하위 에이전트 1개에 위임",
        "one, no model",
    );
    same(of(&[], &none(), &ko), None, "none");
    let model = |list: Vec<MightyGraphAgent>, defs: &HashMap<String, AgentDefinition>| of(&list, defs, &ko).unwrap().model;
    same(
        model(vec![answered_sub("a", "claude-haiku-5-5"), answered_sub("b", "claude-opus-5-5")], &none()).as_deref(),
        Some("모델 2종"),
        "two models",
    );
    same(
        model(vec![sub("a", Sub { resolved: Some("claude-haiku-5-5"), ..Sub::default() }), answered_sub("b", "claude-haiku-5-5")], &none())
            .as_deref(),
        Some("Haiku 5.5"),
        "the resolved model stands in",
    );
    same(
        model(vec![answered_sub("a", "claude-haiku-5-5"), sub("b", Sub { requested: Some("haiku"), ..Sub::default() })], &none())
            .as_deref(),
        Some("Haiku 5.5"),
        "an alias of the named model's family",
    );
    same(
        model(vec![answered_sub("a", "claude-haiku-5-5"), sub("b", Sub { requested: Some("opus"), ..Sub::default() })], &none()).as_deref(),
        Some("모델 2종"),
        "an alias of another family",
    );
    same(
        model(
            vec![
                sub("a", Sub { requested: Some("haiku"), ..Sub::default() }),
                sub("b", Sub { requested: Some("haiku"), ..Sub::default() }),
            ],
            &none(),
        )
        .as_deref(),
        Some("Haiku · 요청"),
        "nothing named yet",
    );
    same(
        model(
            vec![
                sub("a", Sub { requested: Some("haiku"), ..Sub::default() }),
                sub("b", Sub { requested: Some("sonnet"), ..Sub::default() }),
            ],
            &none(),
        )
        .as_deref(),
        Some("모델 2종"),
        "two aliases",
    );
    same(
        model(vec![answered_sub("a", "claude-haiku-5-5"), running_sub("b")], &none()).as_deref(),
        Some("Haiku 5.5"),
        "one that says nothing",
    );
    let definitions = HashMap::from([
        ("reviewer".to_string(), AgentDefinition { name: "reviewer".into(), color: None, model: Some("sonnet".into()) }),
        ("scout".to_string(), AgentDefinition { name: "scout".into(), color: None, model: Some("inherit".into()) }),
    ]);
    same(
        model(vec![sub("a", Sub { kind_type: Some("reviewer"), ..Sub::default() })], &definitions).as_deref(),
        Some("Sonnet · 요청"),
        "a definition's model",
    );
    same(model(vec![sub("a", Sub { kind_type: Some("scout"), ..Sub::default() })], &definitions), None, "inherit asks for none");
    same(model(vec![sub("a", Sub { kind_type: Some("reviewer"), ..Sub::default() })], &none()), None, "without its definition");

    let agents = vec![
        running_sub("first"),
        sub("bg", Sub { background: true, ..Sub::default() }),
        sub("nested", Sub { parent: Some("first"), ..Sub::default() }),
        sub("deep", Sub { parent: Some("nested"), ..Sub::default() }),
        sub("note", Sub { kind: Some("compact"), ..Sub::default() }),
        sub("task", Sub { background: true, kind: Some("task"), ..Sub::default() }),
        sub("orphan", Sub { parent: Some("ghost"), ..Sub::default() }),
        sub("self", Sub { parent: Some("self"), ..Sub::default() }),
    ];
    let shown = support::shown(&plain("r", agents, "running", false), &ko);
    let ids = |key: &str, run: &MightyGraphRun| lines::children(key, run).iter().map(|a| a.id().to_string()).collect::<Vec<_>>();
    same(
        ids(lines::MAIN_KEY, &shown),
        vec!["first", "bg", "task", "orphan", "self"].into_iter().map(String::from).collect(),
        "the main session's, in launch order",
    );
    same(ids("first", &shown), vec!["nested".to_string()], "first's");
    same(ids("nested", &shown), vec!["deep".to_string()], "nested's");
    same(ids("deep", &shown).len(), 0, "deep's");
    same(
        lines::children(
            lines::MAIN_KEY,
            &plain(
                "r:notice:1",
                vec![sub("copy", Sub { status: Some("completed"), background: true, source: Some("bg"), ..Sub::default() })],
                "completed",
                true,
            ),
        )
        .len(),
        0,
        "a notice run delegated nothing",
    );

    let waiting = lines::return_of(
        &plain(
            "r",
            vec![
                running_sub("a"),
                sub("b", Sub { status: Some("completed"), ..Sub::default() }),
                sub("c", Sub { parent: Some("a"), ..Sub::default() }),
            ],
            "running",
            false,
        ),
        &ko,
    )
    .unwrap();
    check(waiting.total == 3 && waiting.running == 2 && waiting.waiting(), "two of three run");
    same(waiting.text.as_str(), "하위 에이전트 2/3 실행 중 · 보고를 기다리는 중", "waiting for reports");
    let back = lines::return_of(
        &plain(
            "r",
            vec![
                sub("a", Sub { status: Some("completed"), ..Sub::default() }),
                sub("b", Sub { status: Some("completed"), ..Sub::default() }),
                sub("c", Sub { parent: Some("a"), status: Some("completed"), ..Sub::default() }),
            ],
            "running",
            false,
        ),
        &ko,
    )
    .unwrap();
    check(!back.waiting(), "all ended");
    same(back.text.as_str(), "메인으로 복귀 · 검토 중", "back to the main session");
    same(lines::return_of(&plain("r", vec![], "running", false), &ko), None, "no delegation");
    same(
        lines::return_of(
            &plain(
                "r",
                vec![
                    sub("note", Sub { kind: Some("compact"), ..Sub::default() }),
                    sub("ask", Sub { kind: Some("question"), ..Sub::default() }),
                ],
                "running",
                false,
            ),
            &ko,
        ),
        None,
        "no sub-agent",
    );
    same(
        lines::return_of(
            &plain(
                "r:notice:1",
                vec![sub("copy", Sub { status: Some("completed"), background: true, source: Some("x"), ..Sub::default() })],
                "running",
                true,
            ),
            &ko,
        ),
        None,
        "a notice run",
    );
    same(
        lines::return_of(
            &support::shown(&plain("r", vec![sub("bg", Sub { background: true, ..Sub::default() })], "running", false), &ko),
            &ko,
        )
        .map(|s| s.total),
        Some(1),
        "a background sub-agent counts through the shown run",
    );
}

/// "flow lines: the delegate line hangs from the request and the main session's card stands where the answer will come"
/// (TheDelegateLineAndTheReturnNodeStandInTheFlow).
#[test]
fn the_delegate_line_and_the_return_node_stand_in_the_flow() {
    let request =
        plain("r", vec![running_sub("a"), sub("b", Sub { background: true, ..Sub::default() }), running_sub("c")], "running", false);
    let graph = flow::flow_layout(std::slice::from_ref(&request), true, &[], true);
    let work = must(&graph, &node_of(&request, "request"));
    let line = must(&graph, &line_id(&request, lines::MAIN_KEY));
    same(line.kind.as_str(), layout::DELEGATE_KIND, "a delegate line");
    check(line.frame.w == lines::DELEGATE_WIDTH && line.frame.h == lines::DELEGATE_HEIGHT, "its size");
    same(line.frame.y, work.frame.max_y() + 18.0, "18 under the block");
    check((line.frame.mid_x() - work.frame.mid_x()).abs() < 0.5, "centred under it");
    check(
        layout::is_fixed(line) && !layout::is_resizable(line) && !layout::is_auxiliary(&line.kind) && !line.is_compact,
        "a label: fixed, never resized",
    );
    check(graph.edges.iter().any(|e| e.source == work.id && e.target == line.id), "the block's branch goes into it");
    for child in ["a", "b", "c"] {
        let card = must(&graph, &agent_id(&request, child));
        check(graph.edges.iter().any(|e| e.source == line.id && e.target == card.id), format!("the line fans out to {child}"));
        check(!graph.edges.iter().any(|e| e.source == work.id && e.target == card.id), format!("not the block to {child}"));
        same(card.frame.y, line.frame.max_y() + 26.0, "the row 26 under the line");
    }
    for other in graph.nodes.iter().filter(|n| n.id != line.id) {
        check(!overlap(&other.frame, &line.frame), format!("nothing overlaps the line: {}", other.id));
    }
    let quiet = flow::flow_layout(
        &[plain("r", vec![sub("note", Sub { status: Some("completed"), kind: Some("compact"), ..Sub::default() })], "running", false)],
        true,
        &[],
        true,
    );
    check(
        !quiet.nodes.iter().any(|n| n.kind == layout::DELEGATE_KIND || n.kind == layout::RETURN_KIND),
        "no line and no return node without a sub-agent",
    );
    let single = plain("r", vec![running_sub("a")], "running", false);
    same(
        plain_layout(std::slice::from_ref(&single), true).nodes.iter().map(|n| n.kind.clone()).collect::<Vec<_>>(),
        vec!["request".to_string(), layout::WORK_KIND.to_string(), "agent".to_string()],
        "without a fan-out, as before",
    );
    same(
        layout::make(std::slice::from_ref(&single), "", true, &HashSet::new(), &Options::default(), &ko())
            .nodes
            .iter()
            .map(|n| n.kind.clone())
            .collect::<Vec<_>>(),
        vec!["request".to_string(), "agent".to_string()],
        "the shared vectors' layout",
    );

    let nested = plain(
        "r",
        vec![
            running_sub("lead"),
            sub("child", Sub { parent: Some("lead"), ..Sub::default() }),
            sub("note", Sub { parent: Some("lead"), status: Some("completed"), kind: Some("compact"), ..Sub::default() }),
        ],
        "running",
        false,
    );
    let lead_id = agent_id(&nested, "lead");
    same(
        find(&flow::flow_layout(std::slice::from_ref(&nested), true, &[], true), &line_id(&nested, "lead")).map(|n| n.id.clone()),
        None,
        "folded: no line",
    );
    let opened = flow::flow_layout(std::slice::from_ref(&nested), true, std::slice::from_ref(&lead_id), true);
    let lead = must(&opened, &lead_id);
    let own = must(&opened, &line_id(&nested, "lead"));
    same(own.frame.y, lead.frame.max_y() + 18.0, "under the opened block");
    check((own.frame.mid_x() - lead.frame.mid_x()).abs() < 0.5, "centred under it");
    for child in ["child", "note"] {
        check(
            opened.edges.iter().any(|e| e.source == own.id && e.target == agent_id(&nested, child)),
            format!("its row hangs off it: {child}"),
        );
    }
    check(find(&opened, &line_id(&nested, lines::MAIN_KEY)).is_some(), "the main line still stands");
    let narrow = flow::flow_layout_with(
        std::slice::from_ref(&nested),
        true,
        std::slice::from_ref(&lead_id),
        true,
        &[],
        Some(HashMap::from([(lead_id.clone(), GraphBlockSize { width: 300.0, height: 200.0 })])),
        None,
    );
    let narrow_line = must(&narrow, &line_id(&nested, "lead"));
    for other in narrow.nodes.iter().filter(|n| n.id != narrow_line.id) {
        check(!overlap(&other.frame, &narrow_line.frame), format!("a narrow block holds its line: {}", other.id));
    }

    let mut running = plain("r", vec![running_sub("a"), running_sub("b")], "running", false);
    let flow_graph = flow::flow_layout(std::slice::from_ref(&running), true, &[], true);
    same(find(&flow_graph, &node_of(&running, layout::RETURN_SUFFIX)).map(|n| n.id.clone()), None, "no return node");
    let back = must(&flow_graph, &node_of(&running, layout::WORK_SUFFIX));
    same(back.kind.as_str(), layout::WORK_KIND, "the 실행 block");
    check(back.is_compact && back.frame.w == steps::CARD_WIDTH && back.frame.h == steps::CARD_HEIGHT, "as its card");
    check(layout::is_fixed(back) && !layout::is_resizable(back) && !layout::is_auxiliary(&back.kind), "fixed");
    let cards: Vec<_> = ["a", "b"].iter().map(|c| must(&flow_graph, &agent_id(&running, c)).clone()).collect();
    for card in &cards {
        check(flow_graph.edges.iter().any(|e| e.source == card.id && e.target == back.id && e.joins), "the branches converge into it");
    }
    same(back.frame.y, cards[0].frame.max_y() + 32.0, "a row gap under the cards");
    check((back.frame.mid_x() - must(&flow_graph, &node_of(&running, "request")).frame.mid_x()).abs() < 0.5, "on the centreline");
    same(find(&flow_graph, &node_of(&running, "result")).map(|n| n.id.clone()), None, "no result yet");
    running.status = Some("completed".into());
    running.final_output = Some("done".into());
    for agent in running.agents.iter_mut().flatten() {
        agent.status = Some("completed".into());
    }
    let done = flow::flow_layout(std::slice::from_ref(&running), false, &[], true);
    same(find(&done, &node_of(&running, layout::WORK_SUFFIX)).map(|n| n.id.clone()), None, "the result takes its place");
    let result = must(&done, &node_of(&running, "result"));
    for child in ["a", "b"] {
        check(
            done.edges.iter().any(|e| e.source == agent_id(&running, child) && e.target == result.id && e.joins),
            format!("into the result: {child}"),
        );
    }

    let awaiting = plain("r", vec![sub("bg", Sub { background: true, ..Sub::default() })], "completed", false);
    let awaited = flow::flow_layout(std::slice::from_ref(&awaiting), true, &[], true);
    same(find(&awaited, &node_of(&awaiting, layout::RETURN_SUFFIX)).map(|n| n.id.clone()), None, "the 알림 대기 block stands there");
    let awaiting_result = must(&awaited, &node_of(&awaiting, "result"));
    check(
        awaited.edges.iter().any(|e| e.source == agent_id(&awaiting, "bg") && e.target == awaiting_result.id && e.joins),
        "the card leads into it",
    );
    let shown_awaiting = support::shown_runs(std::slice::from_ref(&awaiting), &ko()).into_owned();
    check(notice::awaiting_notice(&shown_awaiting[0], &shown_awaiting, &ko()).is_some(), "it awaits the notice");
    let turn = plain("r", vec![sub("bg", Sub { background: true, ..Sub::default() })], "running", false);
    check(
        flow::flow_layout(std::slice::from_ref(&turn), true, &[], true)
            .edges
            .iter()
            .any(|e| e.source == agent_id(&turn, "bg") && e.target == node_of(&turn, layout::WORK_SUFFIX)),
        "while the turn runs, into the main session's card",
    );
    let planned = plain("r", vec![sub("a", Sub { status: Some("completed"), ..Sub::default() })], "running", false);
    let plan_layout = flow::flow_layout_with(std::slice::from_ref(&planned), true, &[], true, &[], None, Some("r"));
    check(
        find(&plan_layout, &node_of(&planned, layout::RETURN_SUFFIX)).is_none()
            && find(&plan_layout, &node_of(&planned, layout::PLAN_SUFFIX)).is_some(),
        "the plan stands there",
    );
    let ended = plain("r", vec![sub("bg", Sub { status: Some("completed"), background: true, ..Sub::default() })], "completed", false);
    let notice_run = plain(
        "r:notice:1",
        vec![sub("r:notice:1:bg", Sub { status: Some("completed"), background: true, source: Some("bg"), ..Sub::default() })],
        "running",
        true,
    );
    let both = flow::flow_layout(&[ended.clone(), notice_run.clone()], true, &[], true);
    check(
        find(&both, &node_of(&notice_run, layout::RETURN_SUFFIX)).is_none()
            && find(&both, &line_id(&notice_run, lines::MAIN_KEY)).is_none(),
        "a notice run has neither",
    );
    check(find(&both, &line_id(&ended, lines::MAIN_KEY)).is_some(), "the request keeps its line");
}

/// "flow lines: the camera follows the main session's card and then the result" (TheCameraFollowsTheReturnNodeAndThenTheResult).
#[test]
fn the_camera_follows_the_return_node_and_then_the_result() {
    let ko = ko();
    let viewport = Size::new(900.0, 600.0);
    let mut request = plain("r", vec![running_sub("a"), running_sub("b")], "running", false);
    let runs = support::shown_runs(std::slice::from_ref(&request), &ko).into_owned();
    let graph = fan_layout(&runs, true);
    let followed = follow::target_id(&graph, &runs);
    same(followed.clone(), Some(node_of(&request, layout::WORK_SUFFIX)), "the main session's card, never a delegate line");
    let followed = followed.unwrap();
    let back = must(&graph, &followed).frame;
    let mut watch = Watch::new();
    let camera = follow::camera_offset(back, viewport, 1.0, false, false, true);
    watch.moved(Some(&followed), Some(back), camera, viewport, 1.0);
    check(watch.following(), "following");
    same(watch.observe(Some(&followed), Some(back), camera, viewport, 1.0), None, "nothing moved");
    request.status = Some("completed".into());
    request.final_output = Some("done".into());
    for agent in request.agents.iter_mut().flatten() {
        agent.status = Some("completed".into());
    }
    let done_runs = support::shown_runs(std::slice::from_ref(&request), &ko).into_owned();
    let done = fan_layout(&done_runs, false);
    let next = follow::target_id(&done, &done_runs);
    same(next.clone(), Some(node_of(&request, "result")), "then the result");
    let next = next.unwrap();
    let result = must(&done, &next).frame;
    let moved = watch.observe(Some(&next), Some(result), camera, viewport, 1.0).expect("the camera should move to the result");
    check((moved.y + result.max_y() - (viewport.h - follow::MARGIN)).abs() < 0.001, "its bottom right above the composer");
    same(moved.x, camera.x, "the reader's x stays");
    let mut away = Watch::new();
    away.moved(Some(&followed), Some(back), Point::new(camera.x, camera.y + 2_000.0), viewport, 1.0);
    check(!away.following(), "a reader who scrolled away");
    same(away.observe(Some(&next), Some(result), Point::new(camera.x, camera.y + 2_000.0), viewport, 1.0), None, "is left there");

    let single = plain("r", vec![running_sub("a")], "running", false);
    let made = flow::flow_layout(std::slice::from_ref(&single), true, &[], true);
    let ids: HashSet<String> = made.nodes.iter().map(|n| n.id.clone()).collect();
    same(camera::reaim_anchor(Some("r"), None, &ids), Anchor::reaim(&node_of(&single, "request"), true), "a re-aim goes to the request");
    same(
        made.newest_request_target_id(std::slice::from_ref(&single)),
        Some(node_of(&single, layout::WORK_SUFFIX)),
        "the newest request's 실행 block",
    );
    let mut stranded = StrandedWatch::new();
    let frames: Vec<(String, camera::GraphRect)> = made.nodes.iter().map(|n| (n.id.clone(), n.frame)).collect();
    same(stranded.observe(&frames, Point::new(0.0, 0.0), 1.0, viewport, Some("r"), true), None, "nobody stranded");
    same(stranded.observe(&frames, Point::new(0.0, 0.0), 1.0, viewport, Some("r"), true), None, "still nobody");
    for i in 0..made.nodes.len() {
        for j in i + 1..made.nodes.len() {
            check(!overlap(&made.nodes[i].frame, &made.nodes[j].frame), format!("{} × {}", made.nodes[i].id, made.nodes[j].id));
        }
    }
}

/// "flow fan-out: a shown run carries what only the flow draws, and every other view reads what it read" (AShownRunCarriesWhatOnlyTheFlowDraws).
/// Its last three checks read the timeline (`MightyTimeline.Groups`), the pipeline (`MightyStructure.Clusters`) and the phone
/// (`MobileMightySupport.Blocks`), which other modules port.
#[test]
fn a_shown_run_carries_what_only_the_flow_draws() {
    let ko = ko();
    let request = plain(
        "r",
        vec![
            fan("fg", None, "completed", false, None, None),
            fan("bg", None, "running", true, None, None),
            fan("bg-child", Some("bg"), "running", false, None, None),
            fan("task", None, "running", true, Some("task"), None),
            fan("task-child", Some("task"), "completed", false, None, None),
            fan("late", None, "completed", false, None, None),
        ],
        "completed",
        false,
    );
    let shown = support::shown(&request, &ko);
    let ids = |list: Vec<&MightyGraphAgent>| list.into_iter().map(|a| a.id().to_string()).collect::<Vec<_>>();
    same(ids(shown.agents().iter().collect()), vec!["fg".to_string(), "late".to_string()], "every other view draws what it drew");
    same(ids(notice::shown_agents(&request)), vec!["fg".to_string(), "late".to_string()], "shownAgents");
    let flow_ids = vec!["fg", "late", "bg", "bg-child", "task", "task-child"].into_iter().map(String::from).collect::<Vec<_>>();
    same(ids(support::flow_agents(&shown)), flow_ids.clone(), "the flow draws them too");
    check(
        support::flow_agents(&shown)[..shown.agents().len()].iter().zip(shown.agents()).all(|(a, b)| *a == b),
        "the same blocks at the same indexes first",
    );
    same(
        (0..support::flow_agents(&shown).len()).map(|i| support::flow_order(&shown, i)).collect::<Vec<_>>(),
        vec![0, 5, 1, 2, 3, 4],
        "each where it was launched",
    );
    same(notice::fan_out(&request), vec![1, 2, 3, 4], "the fan-out");
    check(support::shown(&shown, &ko) == shown, "shown again, it is the same run");
    same(ids(support::flow_agents(&support::shown(&shown, &ko))), flow_ids, "with the same flow");

    let quiet = plain(
        "r",
        vec![fan("a", None, "completed", false, None, None), fan("b", Some("a"), "completed", false, None, None)],
        "completed",
        false,
    );
    let quiet_shown = support::shown(&quiet, &ko);
    check(support::flow_agents(&quiet_shown).into_iter().cloned().collect::<Vec<_>>() == quiet.agents(), "a run that hides nothing");
    same(support::flow_order(&quiet, 1), 1, "its order");
    same(notice::fan_out(&quiet).len(), 0, "no fan-out");
    let notice_run = plain("r:notice:1", vec![fan("copy", None, "completed", true, None, Some("bg"))], "completed", true);
    same(ids(support::flow_agents(&support::shown(&notice_run, &ko))), vec!["copy".to_string()], "a notice run's own");
    same(notice::fan_out(&notice_run).len(), 0, "none for a notice run");
    same(
        ids(support::flow_agents(&support::shown(
            &plain("t", vec![fan("task", None, "completed", true, Some("task"), None)], "completed", false),
            &ko,
        ))),
        vec!["task".to_string()],
        "commands alone fan out",
    );
    let status = plain("r", vec![fan("bg", None, "running", true, None, None)], "completed", false);
    let runs = support::shown_runs(std::slice::from_ref(&status), &ko).into_owned();
    check(runs[0].settled() && support::finished(&runs[0]) && support::has_result(&runs[0]), "it settled, with its result");
    same(support::background_waiting(&runs[0]), 1, "waiting on one");
    check(notice::awaiting_notice(&runs[0], &runs, &ko).is_some(), "awaiting its notice");
}

/// "flow fan-out: the row holds cards and blocks in launch order" (TheFanOutRowHoldsCardsAndBlocks).
#[test]
fn the_fan_out_row_holds_cards_and_blocks() {
    let ko = ko();
    let mixed =
        plain("r", vec![fan("fg", None, "running", false, None, None), fan("bg", None, "running", true, None, None)], "running", false);
    let before = plain_layout(std::slice::from_ref(&mixed), true);
    same(find(&before, &agent_id(&mixed, "bg")).map(|n| n.id.clone()), None, "without a fan-out no background block");
    let fg = must(&before, &agent_id(&mixed, "fg"));
    check(
        fg.frame.w == 360.0 && fg.frame.h == 240.0 && !fg.is_compact && layout::is_resizable(fg) && !layout::is_fixed(fg),
        "the block as before",
    );

    let single = plain("r", vec![fan("bg", None, "running", true, None, None)], "running", false);
    let graph = flow::flow_layout(std::slice::from_ref(&single), true, &[], true);
    let card_node = must(&graph, &agent_id(&single, "bg"));
    check(card_node.is_compact && card_node.frame.w == card::WIDTH && card_node.frame.h == card::HEIGHT, "a card");
    check(!layout::is_resizable(card_node) && layout::is_fixed(card_node), "never resized, fixed");
    let work = must(&graph, &node_of(&single, "request"));
    check(
        graph.edges.iter().any(|e| e.source == work.id && e.target == line_id(&single, lines::MAIN_KEY))
            && graph.edges.iter().any(|e| e.source == line_id(&single, lines::MAIN_KEY) && e.target == card_node.id),
        "through the line",
    );
    same(card_node.frame.y, work.frame.max_y() + LINE_DROP, "under the line");
    check((card_node.frame.mid_x() - work.frame.mid_x()).abs() < 0.5, "centred");

    let finished = plain("r", vec![fan("bg", None, "completed", true, None, None)], "completed", false);
    let stays = flow::flow_layout(std::slice::from_ref(&finished), true, &[], true);
    let ended = must(&stays, &agent_id(&finished, "bg"));
    check(ended.is_compact && ended.frame.w == card::WIDTH, "a finished background card stays");
    let result = must(&stays, &node_of(&finished, "result"));
    check(stays.edges.iter().any(|e| e.source == ended.id && e.target == result.id && e.joins), "it leads into the result");
    same(result.frame.y, ended.frame.max_y() + 32.0, "a row gap under it");

    let mut foreground = plain("r", vec![fan("fg", None, "running", false, None, None)], "running", false);
    check(
        must(&flow::flow_layout(std::slice::from_ref(&foreground), true, &[], true), &agent_id(&foreground, "fg")).is_compact,
        "a running foreground sub-agent is a card",
    );
    foreground.agents.as_mut().unwrap()[0].status = Some("completed".into());
    let ended_id = agent_id(&foreground, "fg");
    check(must(&flow::flow_layout(std::slice::from_ref(&foreground), true, &[], true), &ended_id).is_compact, "and a card once it ends");
    let opened_layout = flow::flow_layout(std::slice::from_ref(&foreground), true, std::slice::from_ref(&ended_id), true);
    let block = must(&opened_layout, &ended_id);
    check(
        !block.is_compact && layout::is_resizable(block) && !layout::is_fixed(block) && block.frame.w == 360.0 && block.frame.h == 240.0,
        "its block once opened",
    );
    let sized = |id: &str, w: f64, h: f64| Some(HashMap::from([(id.to_string(), GraphBlockSize { width: w, height: h })]));
    let saved_layout = flow::flow_layout_with(
        std::slice::from_ref(&foreground),
        true,
        std::slice::from_ref(&ended_id),
        true,
        &[],
        sized(&ended_id, 420.0, 300.0),
        None,
    );
    let saved = must(&saved_layout, &ended_id);
    check(saved.frame.w == 420.0 && saved.frame.h == 300.0, "its saved size is back");
    same(
        must(
            &flow::flow_layout_with(
                std::slice::from_ref(&foreground),
                true,
                std::slice::from_ref(&ended_id),
                true,
                std::slice::from_ref(&ended_id),
                None,
                None,
            ),
            &ended_id,
        )
        .frame
        .h,
        480.0,
        "its opened height",
    );
    let card_only = plain("r", vec![fan("fg", None, "running", false, None, None)], "running", false);
    let card_id = agent_id(&card_only, "fg");
    let ignoring_layout = flow::flow_layout_with(
        std::slice::from_ref(&card_only),
        true,
        &[],
        true,
        std::slice::from_ref(&card_id),
        sized(&card_id, 420.0, 300.0),
        None,
    );
    let ignoring = must(&ignoring_layout, &card_id);
    check(ignoring.frame.w == card::WIDTH && ignoring.frame.h == card::HEIGHT, "a card ignores a saved size and the opened height");

    let pair =
        plain("r", vec![fan("fg", None, "running", false, None, None), fan("bg", None, "running", true, None, None)], "running", false);
    let (fg_id, bg_id) = (agent_id(&pair, "fg"), agent_id(&pair, "bg"));
    let opened_card = flow::flow_layout_with(
        std::slice::from_ref(&pair),
        true,
        std::slice::from_ref(&bg_id),
        true,
        &[],
        sized(&bg_id, 400.0, 260.0),
        None,
    );
    check(must(&opened_card, &fg_id).is_compact, "the other stays a card");
    let opened_block = must(&opened_card, &bg_id);
    check(
        !opened_block.is_compact && layout::is_resizable(opened_block) && opened_block.frame.w == 400.0 && opened_block.frame.h == 260.0,
        "an opened card is its block, saved size and all",
    );
    same(must(&opened_card, &fg_id).frame.y, opened_block.frame.y, "sharing the row's top");

    let agents = vec![
        fan("lead", None, "running", false, None, None),
        fan("child", Some("lead"), "running", false, None, None),
        fan("grand", Some("child"), "running", false, None, None),
        fan("note", Some("lead"), "completed", false, Some("compact"), None),
    ];
    let deep = plain("r", agents.clone(), "running", false);
    let folded = flow::flow_layout(std::slice::from_ref(&deep), true, &[], true);
    check(must(&folded, &agent_id(&deep, "lead")).is_compact, "the lead is a card");
    for hidden in ["child", "grand", "note"] {
        same(find(&folded, &agent_id(&deep, hidden)).map(|n| n.id.clone()), None, &format!("nothing under the card: {hidden}"));
    }
    let mut got: Vec<String> = folded.nodes.iter().map(|n| n.id.clone()).collect();
    got.sort();
    let mut want =
        vec![node_of(&deep, "request"), line_id(&deep, lines::MAIN_KEY), agent_id(&deep, "lead"), node_of(&deep, layout::WORK_SUFFIX)];
    want.sort();
    same(got, want, "four nodes, no stray root");
    same(card::nested(&agents[0], &deep), 2, "the card counts them");
    let lead_id = agent_id(&deep, "lead");
    let opened = flow::flow_layout(std::slice::from_ref(&deep), true, std::slice::from_ref(&lead_id), true);
    check(!must(&opened, &lead_id).is_compact, "opened");
    let child_card = must(&opened, &agent_id(&deep, "child"));
    check(
        child_card.is_compact && find(&opened, &agent_id(&deep, "grand")).is_none() && !must(&opened, &agent_id(&deep, "note")).is_compact,
        "its running child a card, the compaction a block",
    );
    check(
        opened.edges.iter().any(|e| e.source == lead_id && e.target == line_id(&deep, "lead"))
            && opened.edges.iter().any(|e| e.source == line_id(&deep, "lead") && e.target == child_card.id),
        "its own line",
    );
    same(opened.nodes.len(), 7, "seven nodes: no return node, the main session's card instead");

    let order = plain(
        "r",
        vec![
            fan("first", None, "running", false, None, None),
            fan("second", None, "running", true, None, None),
            fan("third", None, "running", false, None, None),
            fan("fourth", None, "running", true, None, None),
        ],
        "running",
        false,
    );
    let row_layout = flow::flow_layout(std::slice::from_ref(&order), true, &[], true);
    let row: Vec<_> = ["first", "second", "third", "fourth"].iter().map(|id| must(&row_layout, &agent_id(&order, id)).clone()).collect();
    let xs: Vec<f64> = row.iter().map(|n| n.frame.x).collect();
    let mut sorted = xs.clone();
    sorted.sort_by(f64::total_cmp);
    same(xs, sorted, "left to right in launch order");
    check(row.iter().all(|n| n.frame.y == row[0].frame.y), "one row");
    for i in 1..row.len() {
        same(row[i].frame.x - row[i - 1].frame.max_x(), 20.0, "the sibling gap");
    }
    check(
        ((row[0].frame.x + row[3].frame.max_x()) / 2.0 - must(&row_layout, &node_of(&order, "request")).frame.mid_x()).abs() < 0.5,
        "centred under the request",
    );
    same(
        support::flow_agents(&support::shown_runs(std::slice::from_ref(&order), &ko)[0])
            .iter()
            .map(|a| a.id().to_string())
            .collect::<Vec<_>>(),
        vec!["first", "third", "second", "fourth"].into_iter().map(String::from).collect::<Vec<_>>(),
        "the visible blocks first",
    );

    let request = plain("r", vec![fan("bg", None, "completed", true, None, None)], "completed", false);
    let notice_run = plain("r:notice:1", vec![fan("r:notice:1:bg", None, "completed", true, None, Some("bg"))], "completed", true);
    let pair_runs = vec![request.clone(), notice_run.clone()];
    let copies = flow::flow_layout(&pair_runs, false, &[], true);
    let copy = must(&copies, &agent_id(&notice_run, "r:notice:1:bg"));
    check(copy.is_compact && copy.frame.w == card::WIDTH && copy.frame.h == card::HEIGHT, "a copy is a card too");
    let opened_copy_layout = flow::flow_layout(&pair_runs, false, std::slice::from_ref(&copy.id), true);
    let opened_copy = must(&opened_copy_layout, &copy.id);
    check(!opened_copy.is_compact && opened_copy.frame.w == 360.0 && opened_copy.frame.h == 240.0, "opened, its full block");
    check(must(&copies, &agent_id(&request, "bg")).is_compact, "the request keeps its card");
    let shown = support::shown_runs(&pair_runs, &ko).into_owned();
    same(card::copy_node_id(support::flow_agents(&shown[0])[0], &shown), Some(copy.id.clone()), "which leads to the copy");

    let commands = plain(
        "r",
        vec![
            fan("task", None, "running", true, Some("task"), None),
            fan("bg", None, "running", true, None, None),
            fan("fg", None, "completed", false, None, None),
        ],
        "running",
        false,
    );
    let command_layout = flow::flow_layout(std::slice::from_ref(&commands), true, &[], true);
    let task = must(&command_layout, &agent_id(&commands, "task")).clone();
    let bg = must(&command_layout, &agent_id(&commands, "bg")).clone();
    let fg_block = must(&command_layout, &agent_id(&commands, "fg")).clone();
    check(task.is_compact && task.frame.w == card::WIDTH && bg.is_compact && fg_block.is_compact, "the command and both agents are cards");
    check(
        task.frame.y == bg.frame.y
            && task.frame.y == fg_block.frame.y
            && task.frame.max_x() < bg.frame.x
            && bg.frame.max_x() < fg_block.frame.x,
        "one row in launch order",
    );
    check(
        find(&command_layout, &line_id(&commands, lines::MAIN_KEY)).is_some()
            && find(&command_layout, &node_of(&commands, layout::WORK_SUFFIX)).is_some(),
        "the line and the main session's card",
    );
    check(
        !must(&flow::flow_layout(std::slice::from_ref(&commands), true, std::slice::from_ref(&task.id), true), &task.id).is_compact,
        "opened, its block",
    );
    check(
        !layout::make(
            &[support::shown(&commands, &ko)],
            "",
            true,
            &HashSet::new(),
            &Options { splits_execution: true, ..Options::default() },
            &ko,
        )
        .nodes
        .iter()
        .any(|n| n.id == task.id),
        "without a fan-out it is not there",
    );

    let mut two =
        plain("r", vec![fan("one", None, "running", true, None, None), fan("two", None, "running", true, None, None)], "running", false);
    let two_runs = support::shown_runs(std::slice::from_ref(&two), &ko).into_owned();
    same(
        follow::target_id(&fan_layout(&two_runs, true), &two_runs),
        Some(node_of(&two, layout::WORK_SUFFIX)),
        "the main session's card under the cards is the lowest",
    );
    two.status = Some("completed".into());
    two.final_output = Some("launched".into());
    let ended_runs = support::shown_runs(std::slice::from_ref(&two), &ko).into_owned();
    let ended_graph = fan_layout(&ended_runs, true);
    same(follow::target_id(&ended_graph, &ended_runs), Some(node_of(&two, "result")), "then the 알림 대기 block");
    same(ended_graph.newest_request_target_id(&ended_runs), Some(node_of(&two, "request")), "the request is the newest request's target");
    let ids: HashSet<String> = ended_graph.nodes.iter().map(|n| n.id.clone()).collect();
    same(camera::reaim_anchor(Some(two.id()), None, &ids), Anchor::reaim(&node_of(&two, "request"), true), "a card is no re-aim target");
}

/// `Command(id, command, title, status, parent, started, ended, exitCode, source)`.
#[allow(clippy::too_many_arguments, reason = "the C# fixture's named arguments")]
fn command(
    id: &str,
    cmd: &str,
    title: Option<&str>,
    status: &str,
    parent: Option<&str>,
    started: Option<i64>,
    ended: Option<i64>,
    exit: Option<i32>,
) -> MightyGraphAgent {
    let mut b = a(id).status(status).background().kind("task").input(cmd).figures(figures(
        started.map(flow::stamp).as_deref(),
        ended.map(flow::stamp).as_deref(),
        None,
        exit,
    ));
    if let Some(p) = parent {
        b = b.parent(p);
    }
    if let Some(t) = title {
        b = b.title(t);
    }
    b.build()
}

const CI: &str = "python3 scripts/release.py ci";

fn running_command(id: &str) -> MightyGraphAgent {
    command(id, CI, None, "running", None, None, None, None)
}

/// `Helper(id, status, background)`: a sub-agent on Haiku 5.5.
fn helper(id: &str, status: &str, background: bool) -> MightyGraphAgent {
    let mut b = a(id).status(status).records(vec![node::response_record(
        &format!("{id}-m"),
        Some("claude-haiku-5-5"),
        GraphTokenUsage { input_tokens: 1, ..GraphTokenUsage::default() },
        &[],
        false,
    )]);
    if background {
        b = b.background();
    }
    b.build()
}

/// "flow tasks: background commands stand in the row, with the line, the way back and the status" (BackgroundCommandsStandInTheRow): every
/// check but its first block, which replays claude-stream-background-commands through the graph tracker and the stream parser.
#[test]
fn background_commands_stand_in_the_row() {
    let ko = ko();
    let cmd = "python3 scripts/release.py ci\n--commit";
    let running = command("ci", cmd, Some("CI 대기"), "running", None, Some(0), None, None);
    let request = r("r").agents(vec![running.clone()]).build();
    let content = card::of(&running, &request, None, &["native-macos 진행 중 · native-windows 진행 중".to_string()], &ko);
    same(content.title.as_str(), "CI 대기", "its description");
    same(content.identity.as_deref(), Some("작업 · python3 scripts/release.py ci"), "its command's first line");
    same(content.activity.as_deref(), Some("native-macos 진행 중 · native-windows 진행 중"), "its last line");
    check(content.running() && content.state_word == "실행 중", "running");
    same(card::figures(&content, flow::date(138), &ko).as_str(), "실행 중 · 2분 18초", "its time");
    same(content.summary.as_str(), "작업 · 백그라운드 · python3 scripts/release.py ci", "in one line");
    check(content.nested == 0 && content.tool_calls.is_none() && content.tokens.is_none(), "no tools, no tokens");
    same(card::of(&running, &request, None, &[], &ko).activity, None, "nothing written yet");
    let figures_of = |agent: MightyGraphAgent, at: i64| card::figures(&card::of(&agent, &request, None, &[], &ko), flow::date(at), &ko);
    same(
        figures_of(command("ci", cmd, None, "completed", None, Some(0), Some(840), Some(0)), 9_999).as_str(),
        "완료 · 종료 코드 0 · 14분 0초",
        "ended",
    );
    same(
        figures_of(command("ci", cmd, None, "error", None, Some(0), Some(75), Some(1)), 9_999).as_str(),
        "실패 · 종료 코드 1 · 1분 15초",
        "failed",
    );
    same(figures_of(command("x", CI, None, "stopped", None, Some(0), Some(5), None), 9).as_str(), "중단 · 5초", "no exit code named");
    same(
        figures_of(command("x", CI, None, "completed", None, None, None, Some(0)), 9).as_str(),
        "완료 · 종료 코드 0",
        "an end the app never saw",
    );
    let bare = MightyGraphAgent {
        id: Some("b".into()),
        title: Some(String::new()),
        input: Some(String::new()),
        status: Some("running".into()),
        kind: Some("task".into()),
        background: Some(true),
        entries: Some(vec![]),
        ..MightyGraphAgent::default()
    };
    same(card::of(&bare, &request, None, &[], &ko).identity.as_deref(), Some("작업"), "a block without a command");
    same(card::of(&bare, &request, None, &[], &ko).title.as_str(), "백그라운드 작업", "and without a description");

    let who =
        roles::task(&command("t", "eas build -p android --profile preview\nsecond line", None, "running", None, None, None, None), &ko)
            .unwrap();
    check(who.role == FlowRole::Task && who.ink == FlowInk::Neutral && who.marks == vec![FlowMark::Background], "a command's identity");
    same(who.summary.as_str(), "작업 · 백그라운드 · eas build -p android --profile preview", "in one line");
    check(who.type_chip.is_none() && who.model_label.is_none() && who.effort.is_none(), "no chip, model or effort");
    same(roles::task(&helper("a", "running", false), &ko), None, "a sub-agent is no command");
    same(roles::role_name(FlowRole::Task, &ko).as_str(), "작업", "its role's name");

    let mixed = of(&[helper("release-notes", "completed", true), running_command("ci"), running_command("apk")], &none(), &ko).unwrap();
    same(mixed.text().as_str(), "3개 병렬 · 하위 에이전트 1 · 작업 2 · 2/3 실행 중", "both kinds");
    check(mixed.head == "3개 병렬" && mixed.model.is_none() && mixed.progress.as_deref() == Some("2/3 실행 중"), "its head, no model");
    same(
        mixed.parts.clone(),
        vec![Part { role: FlowRole::Subagent, text: "하위 에이전트 1".into() }, Part { role: FlowRole::Task, text: "작업 2".into() }],
        "how many of each",
    );
    check(mixed.count == 3 && mixed.running == 2, "counts");
    same(
        of(&[running_command("ci"), command("apk", CI, None, "completed", None, None, None, None)], &none(), &ko)
            .map(|l| l.text())
            .as_deref(),
        Some("작업 2개 실행 · 1/2 실행 중"),
        "commands alone",
    );
    same(
        of(&[command("ci", CI, None, "completed", None, None, None, None)], &none(), &ko).map(|l| l.text()).as_deref(),
        Some("작업 1개 실행"),
        "one ended command",
    );
    same(of(&[running_command("ci")], &none(), &ko).map(|l| l.parts.len()), Some(0), "no parts");
    same(
        of(&[helper("a", "running", false), helper("b", "completed", false)], &none(), &ko).map(|l| l.text()).as_deref(),
        Some("하위 에이전트 2개에 위임 · Haiku 5.5 · 1/2 실행 중"),
        "sub-agents alone",
    );

    let back = |agents: Vec<MightyGraphAgent>, shown: bool| {
        let run = r("r").agents(agents).build();
        let run = if shown { support::shown(&run, &ko) } else { run };
        lines::return_of(&run, &ko)
    };
    let waiting = back(vec![helper("notes", "completed", true), running_command("ci"), running_command("apk")], true).unwrap();
    same(waiting.text.as_str(), "병렬 2/3 실행 중 · 보고를 기다리는 중", "commands among them");
    check(
        waiting.total == 3
            && waiting.running == 2
            && waiting.subagents == 1
            && waiting.subagents_running == 0
            && waiting.tasks == 2
            && waiting.tasks_running == 2,
        "its counts",
    );
    same(
        back(vec![running_command("dev")], true).map(|s| s.text).as_deref(),
        Some("병렬 1/1 실행 중 · 보고를 기다리는 중"),
        "commands alone",
    );
    same(
        back(vec![helper("notes", "completed", true), command("ci", CI, None, "completed", None, None, None, None)], true)
            .map(|s| s.text)
            .as_deref(),
        Some("메인으로 복귀 · 검토 중"),
        "all ended",
    );
    same(
        back(vec![helper("a", "running", false), helper("b", "completed", false)], false).map(|s| s.text).as_deref(),
        Some("하위 에이전트 1/2 실행 중 · 보고를 기다리는 중"),
        "sub-agents alone",
    );
    let mut copy = command("c", CI, None, "completed", None, None, None, None);
    copy.source_block_id = Some("ci".into());
    same(lines::return_of(&r("r:notice:1").agents(vec![copy]).notice().build(), &ko), None, "a notice run");

    let status_of = |run: MightyGraphRun| log::status_of(&support::shown_runs(&[run], &ko), &ko);
    let status =
        status_of(r("r").agents(vec![helper("notes", "completed", true), running_command("ci"), running_command("apk")]).build()).unwrap();
    same(status.text().as_str(), "Sonnet 5.5 실행 중 · 작업 2개 실행 중", "the mock's moment");
    same(status.parts().iter().map(|p| p.role).collect::<Vec<_>>(), vec![FlowRole::Lead, FlowRole::Task], "in their roles' inks");
    same(
        status_of(
            r("r")
                .agents(vec![
                    helper("notes", "running", true),
                    running_command("ci"),
                    command("apk", CI, None, "completed", None, None, None, None),
                ])
                .build(),
        )
        .map(|s| s.text())
        .as_deref(),
        Some("Sonnet 5.5 실행 중 · 하위 에이전트 1/1 실행 중 · 작업 1개 실행 중"),
        "side by side",
    );
    same(
        status_of(
            r("r").agents(vec![helper("notes", "completed", true), command("ci", CI, None, "completed", None, None, None, None)]).build(),
        )
        .map(|s| s.text())
        .as_deref(),
        Some("Sonnet 5.5 실행 중 · 하위 에이전트 1개 끝남"),
        "ended sub-agents once nothing runs",
    );
    same(
        status_of(r("r").status("completed").agents(vec![running_command("ci")]).output("launched").build()).map(|s| s.text()).as_deref(),
        Some("Sonnet 5.5 알림 대기 · 작업 1개 실행 중"),
        "awaiting the commands' notices",
    );

    let mock = r("r")
        .agents(vec![
            helper("notes", "completed", true),
            command("ci", CI, Some("CI 대기"), "running", None, None, None, None),
            command("apk", CI, Some("폰 APK 빌드"), "running", None, None, None, None),
        ])
        .build();
    let mock_graph = flow::flow_layout(std::slice::from_ref(&mock), true, &[], true);
    let row: Vec<_> = ["agent:notes", "agent:ci", "agent:apk"].iter().map(|s| must(&mock_graph, &node_of(&mock, s)).clone()).collect();
    check(row.iter().all(|n| n.is_compact) && row.iter().all(|n| n.frame.y == row[0].frame.y), "three cards in one row");
    check(row[0].frame.max_x() < row[1].frame.x && row[1].frame.max_x() < row[2].frame.x, "left to right");
    let mock_line = must(&mock_graph, &line_id(&mock, lines::MAIN_KEY));
    same(
        mock_graph.edges.iter().filter(|e| e.source == mock_line.id).map(|e| e.target.clone()).collect::<Vec<_>>(),
        row.iter().map(|n| n.id.clone()).collect::<Vec<_>>(),
        "under one line",
    );
    let mock_back = must(&mock_graph, &node_of(&mock, layout::WORK_SUFFIX));
    let mut into: Vec<String> = mock_graph.edges.iter().filter(|e| e.target == mock_back.id).map(|e| e.source.clone()).collect();
    into.sort();
    let mut cards: Vec<String> = row.iter().map(|n| n.id.clone()).collect();
    cards.sort();
    same(into, cards, "into the main session's card");
    same(support::shown_runs(std::slice::from_ref(&mock), &ko)[0].agents().len(), 0, "no command block for the other views");
    check(!plain_layout(std::slice::from_ref(&mock), true).nodes.iter().any(|n| n.id == row[1].id), "nor without the fan-out");

    let nested = r("r")
        .agents(vec![helper("scout", "running", false), command("watch", CI, None, "running", Some("scout"), None, None, None)])
        .build();
    let nested_runs = support::shown_runs(std::slice::from_ref(&nested), &ko).into_owned();
    same(
        lines::children(lines::MAIN_KEY, &nested_runs[0]).iter().map(|a| a.id().to_string()).collect::<Vec<_>>(),
        vec!["scout".to_string()],
        "the sub-agent under the main session",
    );
    same(
        lines::children("scout", &nested_runs[0]).iter().map(|a| a.id().to_string()).collect::<Vec<_>>(),
        vec!["watch".to_string()],
        "the command under the sub-agent",
    );
    let (scout_id, watch_id) = (agent_id(&nested, "scout"), agent_id(&nested, "watch"));
    same(
        find(&flow::flow_layout(std::slice::from_ref(&nested), true, &[], true), &watch_id).map(|n| n.id.clone()),
        None,
        "the card folds it",
    );
    check(
        must(&flow::flow_layout(std::slice::from_ref(&nested), true, &[scout_id], true), &watch_id).is_compact,
        "opened, its row holds the command's card",
    );

    let en = Locale::new("en");
    same(
        of(&[helper("a", "completed", false), running_command("ci"), running_command("apk")], &none(), &en).unwrap().text().as_str(),
        "3 in parallel · sub-agents 1 · tasks 2 · 2/3 running",
        "in English",
    );
    same(
        of(&[running_command("ci")], &none(), &en).map(|l| l.text()).as_deref(),
        Some("Running 1 task · 1/1 running"),
        "one command in English",
    );
    let lines_of = |list: &[&str]| list.iter().map(|s| s.to_string()).collect::<Vec<_>>();
    same(card::last_output(&[]), None, "nothing written");
    same(card::last_output(&lines_of(&["one", "two"])).as_deref(), Some("two"), "the last line");
    same(card::last_output(&lines_of(&["one", "two", "", "   ", "[exited with code 0]"])).as_deref(), Some("two"), "not the exit trailer");
    same(card::last_output(&lines_of(&["[exited with code -1]"])), None, "the trailer alone");
    same(card::last_output(&lines_of(&["  indented  "])).as_deref(), Some("indented"), "trimmed");
    same(
        card::last_output(&lines_of(&["[exited with code soon]"])).as_deref(),
        Some("[exited with code soon]"),
        "only Claude Code's own trailer",
    );
    check(card::last_output(&["x".repeat(500)]).unwrap_or_default().len() <= 240, "bounded");
}
