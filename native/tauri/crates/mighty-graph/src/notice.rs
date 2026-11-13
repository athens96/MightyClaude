//! Finished background work gets a diagram of its own (C# `BackgroundNoticeRuns`, MightyCode.Core/BackgroundNoticeRuns.cs). When a
//! root-level `<task-notification>` (or a background sub-agent's hand-back) opens a Claude turn, the tracker records that turn as a run
//! of its own, `<original>:notice:<n>`, whose children are copies of the finished background blocks (`sourceBlockId`).

use std::collections::HashSet;

use mighty_core::graph::{AwaitedTask, Awaiting, MightyGraphRun, terminal};
use mighty_core::locale::Locale;
use mighty_core::model::RunSession;
use unicode_segmentation::UnicodeSegmentation;

use crate::support;

/// `BackgroundNoticeRuns.Marker`.
pub const MARKER: &str = ":notice:";

/// `BackgroundNoticeRuns.RunID`: the original run's `index`th notice run (from 1).
pub fn run_id(original: &str, index: usize) -> String {
    format!("{original}{MARKER}{index}")
}

/// `BackgroundNoticeRuns.IsNoticeRun`.
pub fn is_notice_run(run_id: &str) -> bool {
    run_id.contains(MARKER)
}

/// `BackgroundNoticeRuns.Opener`: what opened a notice run; it names the run's kind while the run holds no copy.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Opener {
    Work,
    Message,
    Delegation,
}

/// `BackgroundNoticeRuns.NameLimit`.
pub const NAME_LIMIT: usize = 32;

/// `BackgroundNoticeRuns.GenericTitle`.
pub fn generic_title(locale: &Locale) -> String {
    locale.get("graph.background.noticeRun")
}

/// `BackgroundNoticeRuns.Kind(opener)`.
pub fn kind(opener: Opener, locale: &Locale) -> String {
    match opener {
        Opener::Message => locale.get("graph.background.messageRun"),
        Opener::Delegation => locale.get("graph.block.delegationNotice"),
        Opener::Work => locale.get("graph.background.noticeRun"),
    }
}

const LINE_BREAKS: [char; 7] = ['\n', '\u{b}', '\u{c}', '\r', '\u{85}', '\u{2028}', '\u{2029}'];

/// `BackgroundNoticeRuns.Title(kind, names)`: "<kind> · <name>", "<kind> · <first> 외 <n>개", or "<kind>" alone. Each name keeps its first
/// line, cut to `NAME_LIMIT` text elements; equal names are counted, not merged.
pub fn title<S: AsRef<str>>(kind: &str, names: &[S], locale: &Locale) -> String {
    let lines: Vec<&str> =
        names.iter().map(|n| n.as_ref().split(LINE_BREAKS).next().unwrap_or("").trim()).filter(|n| !n.is_empty()).collect();
    let Some(first) = lines.first() else { return kind.to_string() };
    let elements: Vec<&str> = first.graphemes(true).collect();
    let short = if elements.len() > NAME_LIMIT { format!("{}…", elements[..NAME_LIMIT - 1].concat()) } else { first.to_string() };
    let name = if lines.len() == 1 {
        short
    } else {
        locale.get_with("graph.background.noticeRunMore", &[("first", &short), ("count", &(lines.len() - 1).to_string())])
    };
    locale.get_with("graph.background.noticeRunTitle", &[("kind", kind), ("name", &name)])
}

/// `BackgroundNoticeRuns.Heading(run)`: its own title, or the generic one (older saves).
pub fn heading(run: &MightyGraphRun, locale: &Locale) -> String {
    heading_of(run.notice_title.as_deref(), locale)
}

/// `BackgroundNoticeRuns.Heading(title)`.
pub fn heading_of(title: Option<&str>, locale: &Locale) -> String {
    match title {
        Some(t) if !t.trim().is_empty() => t.trim().to_string(),
        _ => generic_title(locale),
    }
}

/// `BackgroundNoticeRuns.Icon`: the task block's symbol.
pub const ICON: &str = "terminal";

/// `BackgroundNoticeRuns.ExecutionTitle`: "실행 · 요청 n", or plain "실행" in a notice run.
pub fn execution_title(notice: bool, ordinal: i32, locale: &Locale) -> String {
    if notice { locale.get("graph.block.execution") } else { locale.get_with("graph.block.executionOf", &[("n", &ordinal.to_string())]) }
}

/// `BackgroundNoticeRuns.OriginalRunID`: the request whose tracker opened `notice_run`, or none for a request.
pub fn original_run_id(notice_run: &str) -> Option<&str> {
    notice_run.find(MARKER).map(|at| &notice_run[..at])
}

/// `AwaitedTask.Word`: 끝나면 알림 while it runs, 알림 전달됨 once it ended, 실패 or 중단됨 when it ended so.
pub fn awaited_word(task: &AwaitedTask, locale: &Locale) -> String {
    locale.get(match task.status.as_str() {
        "error" => "graph.block.taskNotice.failed",
        "stopped" => "graph.block.taskNotice.stopped",
        "completed" => "plan.background.noticeDelivered",
        _ => "plan.background.awaitingNotice",
    })
}

/// `Awaiting.Progress`: "알림 {delivered}/{total}".
pub fn awaiting_progress(awaiting: &Awaiting, locale: &Locale) -> String {
    locale.get_with(
        "graph.block.awaitingNotice.progress",
        &[("delivered", &awaiting.delivered().to_string()), ("total", &awaiting.tasks.len().to_string())],
    )
}

/// `BackgroundNoticeRuns.AwaitingNotice`: a request whose own turn ended completed while background work it started still runs, or ended
/// with a notice turn not answered yet. None otherwise and for a notice run. A shown run carries what it read of the run it was made from.
pub fn awaiting_notice(run: &MightyGraphRun, runs: &[MightyGraphRun], locale: &Locale) -> Option<Awaiting> {
    awaiting(run, runs, Some(locale))
}

fn awaiting(run: &MightyGraphRun, runs: &[MightyGraphRun], locale: Option<&Locale>) -> Option<Awaiting> {
    if run.shown_background_waiting.is_some() {
        return run.shown_awaiting.clone();
    }
    if run.is_notice() || run.status() != "completed" {
        return None;
    }
    let tasks: Vec<_> = run.agents().iter().filter(|a| a.source_block_id.is_none() && (a.is_task() || a.is_background_agent())).collect();
    if tasks.is_empty() {
        return None;
    }
    let answering: HashSet<&str> = runs
        .iter()
        .filter(|n| n.is_notice() && !terminal(n.status()) && original_run_id(n.id()) == Some(run.id()))
        .flat_map(|n| n.agents().iter().filter_map(|copy| copy.source_block_id.as_deref()))
        .collect();
    if !tasks.iter().any(|t| !terminal(t.status()) || answering.contains(t.id())) {
        return None;
    }
    Some(Awaiting {
        tasks: tasks
            .iter()
            .map(|t| AwaitedTask {
                id: t.id().into(),
                title: locale.map_or_else(|| t.title().to_string(), |l| support::block_title(t, l)),
                status: t.status().into(),
            })
            .collect(),
    })
}

/// `BackgroundNoticeRuns.AwaitsNotice`: the pane is alive only for the notices its newest request awaits.
pub fn awaits_notice(pane: &RunSession) -> bool {
    pane.status() == "running"
        && pane
            .graph_runs
            .as_ref()
            .is_some_and(|runs| support::newest_request_run(runs).is_some_and(|newest| awaiting(newest, runs, None).is_some()))
}

/// `BackgroundNoticeRuns.StateWord`.
pub fn state_word(locale: &Locale) -> String {
    locale.get("graph.state.awaitingNotice")
}

/// `BackgroundNoticeRuns.ShownAgents`.
pub use mighty_core::graph::shown_agents;

/// `BackgroundNoticeRuns.FanOut`: the blocks `ShownAgents` hides that the flow diagram still draws, by index into the run's agents.
pub fn fan_out(run: &MightyGraphRun) -> Vec<usize> {
    if run.is_notice() || !run.agents().iter().any(|a| a.background == Some(true)) {
        return vec![];
    }
    let shown: HashSet<&str> = shown_agents(run).into_iter().map(|a| a.id()).collect();
    (0..run.agents().len()).filter(|&i| !shown.contains(run.agents()[i].id())).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn titles_cut_and_count() {
        let ko = Locale::new("ko");
        let kind = kind(Opener::Work, &ko);
        assert_eq!(title::<&str>(&kind, &[], &ko), kind);
        let long = "a".repeat(40);
        let one = title(&kind, &[long.as_str()], &ko);
        assert!(one.contains(&format!("{}…", "a".repeat(31))));
        assert_eq!(original_run_id("r:notice:2"), Some("r"));
        assert_eq!(run_id("r", 3), "r:notice:3");
    }
}
