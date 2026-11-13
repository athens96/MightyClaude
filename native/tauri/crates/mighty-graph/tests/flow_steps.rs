//! A request's checklist as steps on its spine and in its plan column (C# FlowStepsVerification,
//! MightyCode.Core.Tests/FlowStepsVerification.cs).

mod view_support;

use std::collections::HashSet;

use mighty_core::Wire;
use mighty_core::graph::{FlowStep, GraphAgentFigures, MightyGraphAgent, MightyGraphRun};
use mighty_core::json::JsonOut;
use mighty_core::model::{LogEntry, RunSession, TodoItem, TodoProgress};
use mighty_core::time::DateTimeOffset;
use mighty_graph::flow::card;
use mighty_graph::flow::copy;
use mighty_graph::flow::steps::{self, ProgressState};
use mighty_graph::layout::{self, FanOut, Options};
use mighty_graph::support;
use view_support::*;

const T0_TICKS: i64 = 621_355_968_000_000_000 + 1_800_000_000 * 10_000_000;

fn at(seconds: f64) -> DateTimeOffset {
    DateTimeOffset::from_utc_ticks(T0_TICKS + (seconds * 10_000_000.0).round() as i64)
}

fn stamp(seconds: f64) -> String {
    steps::stamp(&at(seconds))
}

fn item(content: &str, status: &str, id: Option<&str>) -> TodoItem {
    TodoItem {
        content: Some(content.into()),
        status: Some(status.into()),
        active_form: Some(format!("{content} 중")),
        id: id.map(str::to_string),
    }
}

fn list(items: Vec<TodoItem>, run: &str) -> TodoProgress {
    TodoProgress { items: Some(items), run_id: Some(run.into()) }
}

fn request(id: &str, status: &str, entries: Vec<LogEntry>, agents: Vec<MightyGraphAgent>) -> MightyGraphRun {
    let mut r = with_agents(run(id, "릴리스", status), agents);
    r.root_entries = Some(entries);
    r.source_run_id = Some(id.into());
    r.provider = Some("claude".into());
    r
}

fn tool_at(id: &str, summary: &str, seconds: f64) -> LogEntry {
    with_activity(
        entry(id, "system", summary, &stamp(seconds), Some("claude")),
        activity(id, "claude", "command", "completed", summary, Some("Bash")),
    )
}

fn fstep(key: &str, content: &str, active: Option<&str>, status: &str, started: Option<String>, ended: Option<String>) -> FlowStep {
    FlowStep {
        key: Some(key.into()),
        content: Some(content.into()),
        active_form: active.map(str::to_string),
        status: Some(status.into()),
        started_at: started,
        ended_at: ended,
    }
}

#[allow(clippy::too_many_arguments, reason = "the C# fixture's named arguments")]
fn block(
    id: &str,
    title: &str,
    status: &str,
    block_kind: Option<&str>,
    bg: Option<bool>,
    started: Option<f64>,
    ended: Option<f64>,
    source: Option<&str>,
) -> MightyGraphAgent {
    MightyGraphAgent {
        id: Some(id.into()),
        title: Some(title.into()),
        input: Some(String::new()),
        status: Some(status.into()),
        entries: Some(vec![]),
        kind: block_kind.map(str::to_string),
        background: bg,
        source_block_id: source.map(str::to_string),
        figures: started.map(|s| GraphAgentFigures {
            started_at: Some(stamp(s)),
            ended_at: ended.map(stamp),
            tool_calls: None,
            exit_code: None,
        }),
        ..MightyGraphAgent::default()
    }
}

/// "flow steps: the progress panel lists the background work the request started" (TheProgressPanelListsTheBackgroundWorkTheRequestStarted).
#[test]
fn the_progress_panel_lists_the_background_work_the_request_started() {
    let ko = ko();
    let blocks = || {
        vec![
            block("fg", "Map the repo", "running", None, None, Some(1.0), None, None),
            block("build", "Build the app", "completed", Some("task"), Some(true), Some(2.0), Some(9.0), None),
            block("wait", "Wait for the deploy", "running", Some("task"), Some(true), Some(3.0), None, None),
            block("review", "Review the diff", "error", None, Some(true), Some(4.0), Some(12.0), None),
            block("copy", "Wait for the deploy", "running", Some("task"), Some(true), None, None, Some("wait")),
        ]
    };
    let r = request("r", "completed", vec![], blocks());
    for shown in [r.clone(), support::shown(&request("r", "completed", vec![], blocks()), &ko)] {
        let (rows, hidden) = steps::background_of(&shown, &ko);
        check(
            rows.iter().map(|r| r.id.as_str()).collect::<Vec<_>>() == ["wait", "review", "build"],
            format!("running first, then the newest ended: {:?}", rows.iter().map(|r| &r.id).collect::<Vec<_>>()),
        );
        check(
            rows.iter().map(|r| r.state).collect::<Vec<_>>() == [ProgressState::Current, ProgressState::Failed, ProgressState::Done],
            "each one's state",
        );
        check(
            rows.iter().map(|r| r.agent).collect::<Vec<_>>() == [false, true, false] && hidden == 0,
            "a command or a sub-agent, nothing left out",
        );
        check(
            rows[1].started_at == Some(at(4.0)) && rows[1].ended_at == Some(at(12.0)) && rows[0].ended_at.is_none(),
            "its times from its figures",
        );
    }
    let progress = steps::progress_of(0, std::slice::from_ref(&r), "요청 1", &ko);
    check(
        progress.background.iter().map(|b| b.text.as_str()).collect::<Vec<_>>()
            == ["Wait for the deploy", "Review the diff", "Build the app"]
            && progress.background_hidden == 0,
        "the panel carries them",
    );
    let page = copy::progress_of(std::slice::from_ref(&r), &ko);
    let page = &page[r.id()];
    check(
        page.background.as_ref().is_some_and(|lines| {
            lines.len() == 3
                && lines[0].id == "wait"
                && lines[0].state == "current"
                && !lines[0].agent
                && lines[0].ended_at.is_none()
                && lines[1].state == "failed"
                && lines[1].started_at == Some(steps::unix_ms(&at(4.0)))
        }) && page.background_hidden == 0,
        "the page's copy carries them",
    );
    check(
        copy::progress_of(&[request("r", "completed", vec![], vec![])], &ko)["r"].background.is_none(),
        "a request without background work sends none",
    );
    let many = request(
        "r",
        "running",
        vec![],
        (0..6).map(|i| block(&format!("t{i}"), &format!("task {i}"), "running", Some("task"), Some(true), None, None, None)).collect(),
    );
    let (rows, hidden) = steps::background_of(&many, &ko);
    check(
        rows.len() == steps::PROGRESS_BACKGROUND_ROWS
            && hidden == 2
            && rows.iter().map(|r| r.id.as_str()).collect::<Vec<_>>() == ["t0", "t1", "t2", "t3"],
        "four lines, in launch order, and a count of the rest",
    );
}

fn steps_of(run: &MightyGraphRun) -> &[FlowStep] {
    run.steps.as_deref().unwrap_or(&[])
}

/// "flow steps: each checklist change keeps the times of the steps it had; an earlier request's done tasks are not this one's"
/// (EachChangeKeepsTheTimesOfTheStepsItHad).
#[test]
fn each_change_keeps_the_times_of_the_steps_it_had() {
    let mut runs = vec![request("r", "running", vec![], vec![])];
    steps::record(&list(vec![item("점검", "in_progress", None), item("노트", "pending", None)], "r"), &mut runs, None, at(0.0));
    steps::record(&list(vec![item("점검", "completed", None), item("노트", "in_progress", None)], "r"), &mut runs, None, at(10.0));
    steps::record(
        &list(vec![item("점검", "completed", None), item("노트", "completed", None), item("CI", "pending", None)], "r"),
        &mut runs,
        None,
        at(25.0),
    );
    let s = steps_of(&runs[0]);
    check(
        s.iter().map(steps::content).collect::<Vec<_>>() == ["점검", "노트", "CI"]
            && s.iter().map(steps::status).collect::<Vec<_>>() == ["completed", "completed", "pending"],
        "the steps and their states",
    );
    check(
        steps::started(&s[0]) == Some(at(0.0))
            && steps::ended(&s[0]) == Some(at(10.0))
            && steps::started(&s[1]) == Some(at(10.0))
            && steps::ended(&s[1]) == Some(at(25.0)),
        "each began and ended when the list said so",
    );
    check(
        s[2].started_at.is_none()
            && s[2].ended_at.is_none()
            && s.iter().map(|x| x.active_form.as_deref().unwrap_or("")).collect::<Vec<_>>() == ["점검 중", "노트 중", "CI 중"],
        "a step not begun, and the active forms",
    );
    let mut skipped = vec![request("r", "running", vec![], vec![])];
    steps::record(&list(vec![item("하나", "in_progress", None), item("둘", "pending", None)], "r"), &mut skipped, None, at(0.0));
    steps::record(&list(vec![item("하나", "completed", None), item("둘", "pending", None)], "r"), &mut skipped, None, at(5.0));
    steps::record(&list(vec![item("하나", "completed", None), item("둘", "completed", None)], "r"), &mut skipped, None, at(9.0));
    check(
        steps::started(&steps_of(&skipped[0])[1]) == Some(at(5.0)) && steps::ended(&steps_of(&skipped[0])[1]) == Some(at(9.0)),
        "a step done at once began at the last end",
    );
    let mut tasks = vec![request("r", "running", vec![], vec![])];
    steps::record(&list(vec![item("빌드", "in_progress", Some("1"))], "r"), &mut tasks, None, at(0.0));
    steps::record(&list(vec![item("빌드와 서명", "in_progress", Some("1"))], "r"), &mut tasks, None, at(4.0));
    let only = steps_of(&tasks[0]);
    check(
        only.len() == 1 && steps::content(&only[0]) == "빌드와 서명" && steps::started(&only[0]) == Some(at(0.0)),
        "the same task, renamed",
    );
    let earlier = TodoProgress { items: Some(vec![item("이전 일", "completed", Some("1"))]), run_id: Some("old".into()) };
    let mut carried = vec![request("old", "completed", vec![], vec![]), request("r", "running", vec![], vec![])];
    steps::record(
        &list(vec![item("이전 일", "completed", Some("1")), item("새 일", "in_progress", Some("2"))], "r"),
        &mut carried,
        Some(&earlier),
        at(0.0),
    );
    steps::record(
        &list(vec![item("이전 일", "completed", Some("1")), item("새 일", "completed", Some("2"))], "r"),
        &mut carried,
        Some(&earlier),
        at(3.0),
    );
    check(
        steps_of(&carried[1]).iter().map(steps::content).collect::<Vec<_>>() == ["새 일"] && carried[0].steps.is_none(),
        "carried over, and stays out",
    );
    let mut elsewhere = vec![request("r", "running", vec![], vec![])];
    steps::record(&list(vec![item("x", "in_progress", None)], "nobody"), &mut elsewhere, None, at(0.0));
    check(elsewhere[0].steps.is_none(), "a list of no run of the pane changes nothing");
}

/// "flow steps: the pane keeps the steps on the request through its events, a graph run and a save" (ThePaneKeepsTheStepsThroughItsEvents):
/// the `todos` event's half (FlowSteps.RecordSteps) and the save. The graph run's half is the pane's record of run events (`AppSnapshot.Apply`),
/// the run engine's.
#[test]
fn the_pane_keeps_the_steps_through_its_events() {
    let pane = RunSession {
        id: Some("s".into()),
        kind: Some("claude".into()),
        provider: Some("claude".into()),
        status: Some("running".into()),
        graph_runs: Some(vec![request("r", "running", vec![], vec![])]),
        ..RunSession::default()
    };
    let after = steps::record_steps(&pane, Some(&list(vec![item("점검", "in_progress", None)], "r")), at(0.0)).unwrap();
    let recorded = &after.graph_runs.as_ref().unwrap()[0];
    check(
        steps_of(recorded).len() == 1
            && steps::status(&steps_of(recorded)[0]) == "in_progress"
            && steps_of(recorded)[0].started_at.is_some(),
        "the step is on the request and the list on the pane",
    );
    let mut out = JsonOut::new();
    recorded.write(&mut out);
    let json = out.finish();
    check(json.contains("\"steps\":[{\"key\":\"text:\\uC810\\uAC80\""), format!("written as steps: {}", &json[..json.len().min(300)]));
    let back = MightyGraphRun::read(&serde_json::from_str(&json).unwrap()).unwrap();
    check(back.steps.as_ref().is_some_and(|s| s.len() == 1 && s[0] == steps_of(recorded)[0]), "read back");
}

/// "flow steps: a step holds what its span holds; its card, the 실행 card and the plan column say it" (AStepHoldsWhatItsSpanHoldsAndItsCardSaysIt).
#[test]
fn a_step_holds_what_its_span_holds_and_its_card_says_it() {
    let ko = ko();
    let spans = vec![
        fstep("a", "a", None, "completed", Some(stamp(10.0)), Some(stamp(20.0))),
        fstep("b", "b", None, "in_progress", Some(stamp(20.4)), None),
        fstep("c", "c", None, "pending", None, None),
    ];
    check(
        steps::step_index(&at(5.0), &spans).is_none()
            && steps::step_index(&at(10.0), &spans) == Some(0)
            && steps::step_index(&at(15.0), &spans) == Some(0),
        "inside the first span",
    );
    check(
        steps::step_index(&at(20.0), &spans) == Some(1) && steps::step_index(&at(99.0), &spans) == Some(1),
        "where two meet, the later; from its whole second",
    );
    let suffix = steps::node_suffix(&fstep("text:릴리스 노트 / 검토", "x", None, "pending", None, None));
    check(
        suffix.starts_with("step:")
            && suffix[5..].chars().all(|c| c.is_ascii_hexdigit())
            && suffix == steps::node_suffix(&fstep("text:릴리스 노트 / 검토", "다른 말", None, "pending", None, None)),
        "a node id never carries the step's words",
    );
    check(steps::stable_hash("") == "cbf29ce484222325" && steps::stable_hash("a") == "af63dc4c8601ec8c", "FNV-1a 64 as the Mac writes it");

    let mut r = request(
        "r",
        "running",
        vec![tool_at("e1", "git status", 1.0), tool_at("e2", "python scripts/release.py check", 3.0), tool_at("e3", "git push", 12.0)],
        vec![],
    );
    r.steps = Some(vec![
        fstep("a", "점검", Some("점검 중"), "completed", Some(stamp(0.0)), Some(stamp(10.0))),
        fstep("b", "푸시", Some("푸시 중"), "in_progress", Some(stamp(10.0)), None),
        fstep("c", "CI", None, "pending", None, None),
    ]);
    let runs = vec![r.clone()];
    let done = steps::card(0, 0, &runs, &ko).unwrap();
    check(
        done.title == "1. 점검"
            && done.status == "completed"
            && done.tool_calls == Some(2)
            && done.activity.as_deref() == Some("python scripts/release.py check"),
        "a done step's card",
    );
    check(card::elapsed(&done, at(99.0), &ko) == Some(card::elapsed_words(at(0.0), at(10.0), &ko)), "its time");
    let now = steps::card(1, 0, &runs, &ko).unwrap();
    check(now.running() && now.tool_calls == Some(1) && now.activity.as_deref() == Some("git push"), "the step in progress");
    let pending = steps::card(2, 0, &runs, &ko).unwrap();
    check(
        pending.is_pending
            && !pending.running()
            && pending.state_word == "예정"
            && pending.activity.is_none()
            && card::elapsed(&pending, at(99.0), &ko).is_none(),
        "a step not begun",
    );
    check(steps::entries(0, 0, &runs).iter().map(|e| e.id()).collect::<Vec<_>>() == ["e1", "e2"], "what the first step holds");
    let mut notice = run("r:notice:1", "", "completed");
    notice.root_entries = Some(vec![tool_at("n1", "gh run watch", 14.0)]);
    notice.source_run_id = Some("r:notice:1".into());
    notice.provider = Some("claude".into());
    notice.notice_run = Some(true);
    check(
        steps::entries(1, 0, &[r.clone(), notice]).iter().map(|e| e.id()).collect::<Vec<_>>() == ["e3", "n1"],
        "a notice turn's records are the activity's too",
    );

    let said = entry("s1", "assistant", "먼저 상태를 봅니다.\n**화면을 찍어 보겠습니다.**", &stamp(2.0), Some("claude"));
    let mut lead = request("r", "running", vec![tool_at("l1", "ls", 1.0), said], vec![]);
    let content = steps::lead_card(&lead, "실행", &ko);
    check(content.activity.as_deref() == Some("화면을 찍어 보겠습니다.") && content.prose, "what it last said, as prose");
    lead.root_entries.as_mut().unwrap().push(tool_at("l2", "screencapture -x shot.png", 3.0));
    let content = steps::lead_card(&lead, "실행", &ko);
    check(content.activity.as_deref() == Some("screencapture -x shot.png") && !content.prose, "a newer command, as code");
    let mut answered = request("r", "completed", vec![tool_at("x1", "ls", 1.0), tool_at("x2", "make", 2.0)], vec![]);
    answered.final_output = Some("완료했습니다.\n자세한 내용".into());
    let content = steps::lead_card(&answered, "실행 · 요청 1", &ko);
    check(
        content.activity.as_deref() == Some("완료했습니다.") && content.prose && content.tool_calls == Some(2) && !content.running(),
        "ended: its answer",
    );

    let progress = steps::progress_of(0, &runs, "요청 27", &ko);
    check(
        progress.title == "진행 상황"
            && progress.heading == "요청 27"
            && progress.count.as_deref() == Some("1/3")
            && progress.running
            && progress.status == "running"
            && progress.state_word == "실행 중",
        "its head and its request's state",
    );
    check(
        progress.rows.iter().map(|r| r.text.as_str()).collect::<Vec<_>>() == ["1. 점검", "2. 푸시 중", "3. CI"]
            && progress.rows.iter().map(|r| r.state).collect::<Vec<_>>()
                == [ProgressState::Done, ProgressState::Current, ProgressState::Pending],
        "every step and its state",
    );
    let mut stopped = request("r", "stopped", r.root_entries().to_vec(), vec![]);
    stopped.steps = r.steps.clone();
    let halted = steps::progress_of(0, &[stopped], "요청 27", &ko);
    check(
        halted.rows[1].state == ProgressState::Stopped && halted.state_word == "중단" && !halted.running,
        "stopped with a step in progress",
    );
    let mut long_run = request("r", "running", vec![], vec![]);
    long_run.steps = Some(
        (0..30)
            .map(|i| {
                let status = if i < 20 {
                    "completed"
                } else if i == 20 {
                    "in_progress"
                } else {
                    "pending"
                };
                fstep(&i.to_string(), &format!("단계 {i}"), None, status, (i <= 20).then(|| stamp(i as f64)), None)
            })
            .collect(),
    );
    let window = steps::progress_of(0, &[long_run], "요청 1", &ko);
    check(
        window.rows.len() == steps::PROGRESS_VISIBLE_ROWS - 1
            && window.rows.iter().any(|r| r.state == ProgressState::Current)
            && window.hidden + window.rows.len() == 30,
        "11 steps and a line for the rest",
    );
    let plain = steps::progress_of(0, &[request("r", "completed", vec![], vec![])], "요청 2", &ko);
    check(
        plain.rows.is_empty() && plain.count.is_none() && plain.state_word == "완료" && plain.request == "릴리스",
        "no plan: its words and its state, no rows",
    );

    let mut notes = agent("notes", "notes", "노트", "running");
    notes.figures = Some(GraphAgentFigures { started_at: Some(stamp(12.0)), ..GraphAgentFigures::default() });
    let mut early = agent("early", "early", "먼저", "completed");
    early.figures = Some(GraphAgentFigures { started_at: Some(stamp(-5.0)), ..GraphAgentFigures::default() });
    let mut in_step = request("r", "running", vec![], vec![notes, early]);
    in_step.steps = Some(vec![
        fstep("a", "점검", None, "completed", Some(stamp(0.0)), Some(stamp(10.0))),
        fstep("b", "노트", None, "in_progress", Some(stamp(10.0)), None),
    ]);
    check(
        steps::step_label(&in_step.agents()[0], &in_step, &ko).as_deref() == Some("2. 노트")
            && steps::step_label(&in_step.agents()[1], &in_step, &ko).is_none(),
        "the step a card was launched in",
    );
    let made = layout::make(
        std::slice::from_ref(&in_step),
        "",
        true,
        &HashSet::new(),
        &Options { splits_execution: true, fan_out: Some(FanOut::default()), ..Options::default() },
        &ko,
    );
    let frame = |suffix: &str| made.nodes.iter().find(|n| n.id.ends_with(suffix)).unwrap().frame;
    check(
        frame("agent:notes").w == 236.0
            && frame("agent:notes").h == 104.0
            && frame("agent:early").w == 236.0
            && frame("agent:early").h == 86.0
            && made.nodes.iter().all(|n| n.kind != "outline"),
        "its card has one more line, and the plan is no block",
    );
}

/// "flow steps: a saved heartbeat block is dropped on read, saved steps are bounded" (ASavedHeartbeatBlockIsDroppedAndSavedStepsAreBounded).
#[test]
fn a_saved_heartbeat_block_is_dropped_and_saved_steps_are_bounded() {
    let mut heartbeat = agent("graph-bash", "하위 에이전트", "", "completed");
    heartbeat.entries = Some(vec![entry("graph-bash-answer", "assistant", "Build complete!", "2026-11-11T15:17:41Z", Some("claude"))]);
    heartbeat.figures = Some(GraphAgentFigures {
        started_at: Some("2026-11-11T15:17:41Z".into()),
        ended_at: Some("2026-11-11T15:19:02.999Z".into()),
        ..GraphAgentFigures::default()
    });
    let real = agent("graph-agent", "하위 에이전트", "Map the repo", "running");
    let titled = agent("graph-named", "Map", "", "completed");
    let mut r = with_agents(run("r", "요청", "completed"), vec![heartbeat, real, titled]);
    r.provider = Some("claude".into());
    let mut budget = 10_000_000;
    let kept = support::normalized(&[r], true, &mut budget, None);
    check(kept[0].agents().iter().map(|a| a.id()).collect::<Vec<_>>() == ["graph-agent", "graph-named"], "only the heartbeat's block goes");
    let mut stepped = run("s", "요청", "completed");
    stepped.provider = Some("claude".into());
    stepped.steps = Some(
        (0..150)
            .map(|i| {
                fstep(
                    &format!("text:{i}"),
                    &format!("단계 {i}"),
                    None,
                    if i == 0 { "done?" } else { "completed" },
                    Some("not a time".into()),
                    None,
                )
            })
            .collect(),
    );
    budget = 10_000_000;
    let saved = support::normalized(&[stepped], true, &mut budget, None);
    let s = steps_of(&saved[0]);
    check(
        s.len() == 100 && steps::status(&s[0]) == "pending" && s.iter().all(|x| x.started_at.is_none()),
        "as many as a list holds, statuses and times checked",
    );
}

/// "flow steps: an ACP agent's plan update is its checklist and the request's steps" (AnACPAgentsPlanIsTheRequestsSteps): the recording onto
/// an opencode pane. The plan update read into a checklist (`TodoProgressTracker`) is the run engine's.
#[test]
fn an_acp_agents_plan_is_the_requests_steps() {
    let progress = TodoProgress {
        items: Some(vec![
            TodoItem { content: Some("Read the README".into()), status: Some("completed".into()), active_form: None, id: None },
            TodoItem { content: Some("Summarize it".into()), status: Some("in_progress".into()), active_form: None, id: None },
            TodoItem { content: Some("Answer".into()), status: Some("pending".into()), active_form: None, id: None },
        ]),
        run_id: Some("r".into()),
    };
    let mut graph = run("r", "요약", "running");
    graph.source_run_id = Some("r".into());
    graph.provider = Some("opencode".into());
    let pane = RunSession {
        id: Some("s".into()),
        kind: Some("claude".into()),
        provider: Some("opencode".into()),
        graph_runs: Some(vec![graph]),
        ..RunSession::default()
    };
    let recorded = steps::record_steps(&pane, Some(&progress), at(1.0)).unwrap();
    check(
        steps_of(&recorded.graph_runs.unwrap()[0]).iter().map(steps::status).collect::<Vec<_>>() == ["completed", "in_progress", "pending"],
        "an opencode pane keeps the steps on its request",
    );
}
