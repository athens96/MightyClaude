//! The 파이프라인 view's state colours against C# `MightyStructureToneVerification`
//! (native/windows/MightyCode.Core.Tests/MightyStructureToneVerification.cs, registered in Verification.cs:368-373).

mod common;

use common::*;
use mighty_core::graph::MightyGraphRun;
use mighty_core::shell::Tone;
use mighty_graph::structure::board::{self, Board, BoardColumn};
use mighty_graph::structure::{self, Inputs, Step, StructureStyle, tone};

fn words(tones: impl IntoIterator<Item = Option<Tone>>) -> String {
    tones.into_iter().map(|t| t.map_or("-", Tone::name)).collect::<Vec<_>>().join(",")
}

fn boards(runs: &[MightyGraphRun], style: Option<&ManifestStyle>, current: Option<&str>, starting_new: bool) -> Vec<Board> {
    let inputs = Inputs {
        style: style.map(|s| s as &dyn StructureStyle),
        current_phase: current.and_then(|id| style.map(|s| phase(s, id))),
        starting_new,
        ..Inputs::default()
    };
    board::boards(&structure::make(runs, &inputs, None, &ko()), &ko())
}

fn columns(board: &Board) -> String {
    words(board.columns.iter().map(tone::of_column))
}

fn cards(column: &BoardColumn) -> String {
    words(column.cards.iter().map(|c| tone::of_card(c, column)))
}

#[test]
fn a_status_and_a_step_take_the_pipeline_tone() {
    let statuses = words(
        [
            "running",
            "starting",
            "queued",
            "waiting",
            "completed",
            "done",
            "error",
            "failed",
            "stopped",
            "cancelled",
            "interrupted",
            "pending",
        ]
        .map(tone::of_status),
    );
    assert_eq!(
        statuses, "run,run,run,wait,done,done,err,err,stop,stop,stop,-",
        "a status takes the Mac's block bucket, done is done and pending none"
    );
    let steps = words([
        tone::of_step(Step::Done, false),
        tone::of_step(Step::Current, false),
        tone::of_step(Step::Current, true),
        tone::of_step(Step::Error, false),
        tone::of_step(Step::Stopped, false),
        tone::of_step(Step::Pending, false),
        tone::of_step(Step::Pending, true),
        tone::of_step(Step::Done, true),
    ]);
    assert_eq!(steps, "done,run,wait,err,stop,-,-,done", "a step's tone, the current one waiting while its request waits");
}

#[test]
fn a_free_board_tints_its_columns_and_cards() {
    let board = &boards(&[run("r1", None, "waiting", vec![agent("a", None, "running", None)], None, None)], None, None, false)[0];
    assert_eq!(columns(board), "done,wait,-", "a waiting request: 요청 done, 실행 waits, 결과 to come");
    assert_eq!(cards(&board.columns[1]), "wait,run", "the 실행 card waits, its running sub-agent runs");
    assert!(cards(&board.columns[0]) == "done" && cards(&board.columns[2]) == "-", "the 요청 and 결과 cards take their column's tone");
    let made = boards(
        &[
            run("r1", None, "completed", vec![], Some("끝"), None),
            status("r2", "failed"),
            status("r3", "cancelled"),
            status("r4", "running"),
        ],
        None,
        None,
        false,
    );
    let ended = made.iter().map(columns).collect::<Vec<_>>().join("|");
    assert_eq!(ended, "done,done,done|done,err,err|done,stop,stop|done,run,-", "a free board by how its request ended");
    let results = words(made.iter().map(|b| tone::of_card(&b.columns[2].cards[0], &b.columns[2])));
    assert_eq!(results, "done,err,stop,-", "the 결과 card by how its request ended");
}

#[test]
fn a_pipeline_tints_its_phases_and_each_card_by_its_own_status() {
    let flow = style("ouroboros");
    let waiting = &boards(
        &[
            run("r1", Some("ooo interview 시작"), "completed", vec![sub("x"), agent("y", Some("x"), "failed", None)], None, None),
            run("r2", Some("ooo seed"), "waiting", vec![], None, None),
        ],
        Some(&flow),
        None,
        false,
    )[0];
    assert_eq!(columns(waiting), "done,done,wait,-,-,-", "the phases done, the waiting request's phase waits, the rest to come");
    assert_eq!(cards(&waiting.columns[1]), "done,done,err", "each card its own status's");
    let running =
        &boards(&[asked("r1", "ooo interview 시작"), run("r2", Some("ooo seed"), "running", vec![], None, None)], Some(&flow), None, false)
            [0];
    assert_eq!(columns(running), "done,done,run,-,-,-", "the running request's phase runs");
}

/// The words the host sends (a tone lowercased; the structure's tones never give idle) are the ones the page turns into classes
/// (native/windows/web/src/mighty/structureTone.ts `tones`), so no state is dropped on the way.
#[test]
fn the_hosts_tone_words_are_the_pages_classes() {
    let mut host: Vec<&str> = [Tone::Run, Tone::Wait, Tone::Done, Tone::Err, Tone::Stop].map(Tone::name).to_vec();
    host.sort();
    let path = repo().join("native/windows/web/src/mighty/structureTone.ts");
    let source = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    let marker = "const tones = new Set([";
    let start = source.find(marker).expect("structureTone.ts names its tones in `const tones = new Set([...])`") + marker.len();
    let end = start + source[start..].find(']').expect("the set closes");
    let mut page: Vec<&str> = source[start..end].split(',').map(|w| w.trim().trim_matches('\'')).filter(|w| !w.is_empty()).collect();
    page.sort();
    assert_eq!(host, page, "host vs page");
}

#[test]
fn a_current_column_takes_its_newest_requests_status() {
    let flow = style("ouroboros");
    // The pipeline stands at 시드 with its request over: the step reads as that request ended.
    let seed: Vec<Option<Tone>> = ["completed", "stopped", "cancelled", "failed"]
        .iter()
        .map(|s| {
            let board =
                boards(&[asked("r1", "ooo interview 시작"), run("r2", Some("ooo seed"), s, vec![], None, None)], Some(&flow), None, false)
                    .remove(0);
            assert_eq!(board.columns[2].step.step, Step::Current, "{s}: the pipeline stands at 시드");
            tone::of_column(&board.columns[2])
        })
        .collect();
    assert_eq!(words(seed), "done,stop,stop,err", "the current step by how its newest request ended");
    // The pane stands at 실행 for the next request, which no request reached yet: the current step runs.
    let moved = boards(&[asked("r1", "ooo interview 시작"), asked("r2", "ooo seed")], Some(&flow), Some("run"), true).remove(0);
    assert!(moved.columns[3].step.step == Step::Current && moved.columns[3].cards.is_empty(), "the pane's next phase is current and empty");
    assert_eq!(tone::of_column(&moved.columns[3]), Some(Tone::Run), "an empty current step runs");
}

#[test]
fn a_current_column_runs_until_its_newest_request_really_finished() {
    // The request reports completed, but a sub-agent still runs: not finished, so the step runs.
    let flow = style("ouroboros");
    let seed = boards(
        &[asked("r1", "ooo interview 시작"), run("r2", Some("ooo seed"), "completed", vec![agent("a", None, "running", None)], None, None)],
        Some(&flow),
        None,
        false,
    )
    .remove(0);
    assert_eq!(seed.columns[2].step.step, Step::Current, "the pipeline stands at 시드");
    assert_eq!(tone::of_column(&seed.columns[2]), Some(Tone::Run), "a completed request whose sub-agent still runs keeps its step running");
    let free =
        boards(&[run("r1", None, "completed", vec![agent("bg", None, "running", Some("task"))], None, None)], None, None, false).remove(0);
    assert_eq!(columns(&free), "done,run,-", "a completed free request whose task still runs: 실행 runs");
}
