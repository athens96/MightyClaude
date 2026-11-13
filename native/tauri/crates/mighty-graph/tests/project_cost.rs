//! What one push of a large pane costs (`view::project`, the 120 ms projection): a pane of requests, each with its steps, 200 entries of its
//! own, 20 blocks (five of them background commands a notice run reports) and a notice run, projected in each presentation. The timed run
//! is ignored by default (`cargo test -p mighty-graph --release --test project_cost -- --ignored --nocapture`); the check that runs always
//! holds what the projection's single copy of the shown runs rests on: the views read shown runs as they read the raw ones.

mod common;

use std::borrow::Cow;
use std::collections::{HashMap, HashSet};
use std::hash::{DefaultHasher, Hash, Hasher};
use std::time::Instant;

use common::{agent, ko};
use mighty_core::graph::{FlowStep, GraphAgentFigures, MightyGraphAgent, MightyGraphRun};
use mighty_core::model::{LogEntry, RunSession};
use mighty_core::time::DateTimeOffset;
use mighty_graph::flow::{copy, steps};
use mighty_graph::images::AgentImageLocation;
use mighty_graph::layout::{self, FanOut, Options};
use mighty_graph::support;
use mighty_graph::view::*;

const STEPS: usize = 8;
const ENTRIES: usize = 200;
const BLOCKS: usize = 20;
const BACKGROUND: usize = 5;
const T0_TICKS: i64 = 621_355_968_000_000_000 + 1_800_000_000 * 10_000_000;

fn stamp(seconds: i64) -> String {
    steps::stamp(&DateTimeOffset::from_utc_ticks(T0_TICKS + seconds * 10_000_000))
}

fn said(id: &str, seconds: i64) -> LogEntry {
    LogEntry::new(id.into(), "assistant", format!("{id}: {}", "결과를 정리했습니다. ".repeat(20)), stamp(seconds), None)
}

fn block(id: &str, background: bool, status: &str, start: i64, entries: Vec<LogEntry>) -> MightyGraphAgent {
    MightyGraphAgent {
        kind: background.then(|| "task".to_string()),
        background: background.then_some(true),
        input: Some(format!("npm run build {id}")),
        title: Some(format!("Build {id}")),
        entries: Some(entries),
        figures: Some(GraphAgentFigures {
            started_at: Some(stamp(start)),
            ended_at: Some(stamp(start + 30)),
            tool_calls: None,
            exit_code: None,
        }),
        ..agent(id, None, status, None)
    }
}

/// Request `index` and the notice run that reports its background commands; the last request's commands still run.
fn request(index: usize, last: bool) -> [MightyGraphRun; 2] {
    let id = format!("r{index}");
    let base = index as i64 * 10_000;
    let steps: Vec<FlowStep> = (0..STEPS)
        .map(|s| FlowStep {
            key: Some(format!("s{s}")),
            content: Some(format!("단계 {s}")),
            active_form: Some(format!("단계 {s} 중")),
            status: Some(if last && s + 1 == STEPS { "in_progress" } else { "completed" }.into()),
            started_at: Some(stamp(base + s as i64 * 600)),
            ended_at: (!last || s + 1 < STEPS).then(|| stamp(base + s as i64 * 600 + 590)),
        })
        .collect();
    let entries: Vec<LogEntry> = (0..ENTRIES).map(|e| said(&format!("{id}-e{e}"), base + e as i64 * 24)).collect();
    let agents: Vec<MightyGraphAgent> = (0..BLOCKS)
        .map(|b| {
            let own: Vec<LogEntry> = (0..20).map(|e| said(&format!("{id}-b{b}-e{e}"), base + b as i64 * 200 + e)).collect();
            let status = if last && b < BACKGROUND { "running" } else { "completed" };
            block(&format!("{id}-b{b}"), b < BACKGROUND, status, base + b as i64 * 200, own)
        })
        .collect();
    let mut run = common::run(&id, None, "completed", agents, Some("끝났습니다."), Some(&id));
    run.root_entries = Some(entries);
    run.steps = Some(steps);
    let notice_id = format!("{id}:notice:1");
    let copies: Vec<MightyGraphAgent> = (0..BACKGROUND)
        .map(|b| MightyGraphAgent {
            source_block_id: Some(format!("{id}-b{b}")),
            ..block(&format!("{notice_id}:b{b}"), true, "completed", base + 9_000, vec![])
        })
        .collect();
    let status = if last { "running" } else { "completed" };
    let mut notice = common::run(&notice_id, Some("[Mighty Code task notice] 끝"), status, copies, Some("확인했습니다."), Some(&notice_id));
    notice.notice_run = Some(true);
    notice.root_entries = Some((0..20).map(|e| said(&format!("{id}-n{e}"), base + 9_000 + e as i64)).collect());
    [run, notice]
}

fn runs(requests: usize) -> Vec<MightyGraphRun> {
    (0..requests).flat_map(|i| request(i, i + 1 == requests)).collect()
}

fn pane(requests: usize, presentation: Option<&str>) -> RunSession {
    RunSession {
        id: Some("pane".into()),
        kind: Some("claude".into()),
        provider: Some("claude".into()),
        status: Some("idle".into()),
        resume_id: Some("session-1".into()),
        graph_runs: Some(runs(requests)),
        graph_view_mode: presentation.map(str::to_string),
        ..RunSession::default()
    }
}

fn refused(_: &str, _: Option<&str>) -> AgentImageLocation {
    AgentImageLocation::Refused
}

type Definitions = HashMap<String, mighty_graph::flow::roles::AgentDefinition>;

fn sources<'a>(tails: &'a HashMap<String, Vec<String>>, definitions: &'a Definitions) -> Sources<'a> {
    Sources {
        older: &[],
        history: None,
        pending_plan: false,
        executions: &[],
        dashboard: &NoDashboard,
        root: None,
        images: &refused,
        tails,
        definitions,
        catalog: None,
        style: None,
        animations: true,
        zone: None,
    }
}

/// What the page is sent, as one number: the blocks, the inputs and the presentation's own view, written as JSON.
fn digest(made: &MightyProjection) -> u64 {
    let mut hasher = DefaultHasher::new();
    serde_json::to_string(&made.blocks).expect("blocks").hash(&mut hasher);
    serde_json::to_string(&made.inputs).expect("inputs").hash(&mut hasher);
    serde_json::to_string(&made.timeline).expect("timeline").hash(&mut hasher);
    serde_json::to_string(&made.structure).expect("structure").hash(&mut hasher);
    hasher.finish()
}

/// The projection makes the runs as views draw them once and hands them to the diagram, its copy, the log strip and the progress panel,
/// which each show them again: shown runs show as they are (no copy), and every view reads them as it reads the raw runs.
#[test]
fn the_views_read_shown_runs_as_they_read_the_raw_ones() {
    let ko = ko();
    let raw = runs(3);
    let shown = support::shown_runs(&raw, &ko);
    assert!(matches!(shown, Cow::Owned(_)), "the requests hide their background commands");
    assert!(matches!(support::shown_runs(&shown, &ko), Cow::Borrowed(_)), "shown again, the same runs, not a copy");
    assert!(matches!(support::with_background_waiting(&shown), Cow::Borrowed(_)), "counted again, the same runs");
    assert_eq!(support::shown_runs(&shown, &ko).as_ref(), shown.as_ref());
    for run in shown.iter() {
        assert!(matches!(support::shown_in_borrowed(run, &shown, &ko), Cow::Borrowed(_)));
    }
    let options = Options { splits_execution: true, fan_out: Some(FanOut::open_all()), ..Options::default() };
    let made = layout::make(&raw, "", true, &HashSet::new(), &options, &ko);
    assert_eq!(layout::make(&shown, "", true, &HashSet::new(), &options, &ko), made, "the diagram");
    let none = Definitions::new();
    assert_eq!(copy::blocks(&made, &shown, true, &none, &|_| vec![], &ko), copy::blocks(&made, &raw, true, &none, &|_| vec![], &ko));
    assert_eq!(copy::progress_of(&shown, &ko), copy::progress_of(&raw, &ko), "the progress panel");
    assert_eq!(copy::log_of(&shown, false, &none, None, &ko), copy::log_of(&raw, false, &none, None, &ko), "the log strip");
    assert_eq!(mighty_graph::timeline::groups(&shown, &ko), mighty_graph::timeline::groups(&raw, &ko), "기록");
    // A step's cards and what it holds, read once for all the steps.
    let held = steps::entries_by_step(4, &raw);
    assert_eq!(held.len(), STEPS);
    for (step, entries) in held.iter().enumerate() {
        let ids: Vec<&str> = entries.iter().map(|e| e.id()).collect();
        assert_eq!(ids, steps::entries(step, 4, &raw).iter().map(|e| e.id()).collect::<Vec<_>>());
        assert_eq!(steps::card_held(step, 4, &raw, entries, &ko), steps::card(step, 4, &raw, &ko));
    }
    assert!(held[STEPS - 1].iter().any(|e| e.id().starts_with("r2-n")), "the notice turn's entries in the step it fell in");
}

#[test]
#[ignore = "timing: run with --release --ignored --nocapture"]
fn a_large_pane_projects_within_its_push() {
    let ko = ko();
    let (tails, definitions) = (HashMap::new(), Definitions::new());
    for presentation in [None, Some("timeline"), Some("structure")] {
        let shown = pane(30, presentation);
        let mut state = MightyPane::new();
        let warm = project(&shown, &mut state, &sources(&tails, &definitions), &ko);
        let mut times: Vec<f64> = (0..15)
            .map(|_| {
                let started = Instant::now();
                let again = project(&shown, &mut state, &sources(&tails, &definitions), &ko);
                assert_eq!(again.blocks.len(), warm.blocks.len());
                started.elapsed().as_secs_f64() * 1_000.0
            })
            .collect();
        times.sort_by(f64::total_cmp);
        let name = presentation.unwrap_or("diagram");
        println!(
            "project {name:<10} best {:7.1} ms, median {:7.1} ms per push ({} blocks, digest {:016x})",
            times[0],
            times[times.len() / 2],
            warm.blocks.len(),
            digest(&warm)
        );
    }
}
