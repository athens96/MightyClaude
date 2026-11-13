//! The Mighty 기록 view: the diagram's requests and blocks in their recorded order (C# `MightyTimeline`,
//! MightyCode.Core/MightyTimeline.cs; macOS MightyTimeline). The page draws each group as a request row, its 실행 row, a row per block
//! and the result head; this builds what each says.

use std::collections::HashSet;

use mighty_core::graph::{GRAPH_PROVIDERS, MightyGraphRun, WORK_SUFFIX};
use mighty_core::locale::Locale;
use mighty_core::model::{LogEntry, RunSession};
use mighty_core::normalize::activity_clean;
use mighty_core::shell::Tone;
use mighty_core::time;
use unicode_segmentation::UnicodeSegmentation;

use crate::notice;
use crate::support;

/// `MightyTimeline.Node(Tone, Ring, Rail)`: a row's mark — its tone, whether it rings (runs), and the rail it lights below it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Node {
    pub tone: Tone,
    pub ring: bool,
    pub rail: Option<Tone>,
}

/// `MightyTimeline.Row(NodeId, AgentIndex, Kind, Title, Status, Node, Latest, DurationMs, IsExecution)`. `agent_index` is none for the
/// request and its 실행 block; `is_execution` is the 실행 block, the main agent's own work under the request.
#[derive(Debug, Clone, PartialEq)]
pub struct Row {
    pub node_id: String,
    pub agent_index: Option<usize>,
    pub kind: String,
    pub title: String,
    pub status: String,
    pub node: Node,
    pub latest: Option<String>,
    pub duration_ms: Option<f64>,
    pub is_execution: bool,
}

/// `MightyTimeline.Tally(Total, Settled, Running)`: the blocks present; the request itself is not one of them.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Tally {
    pub total: usize,
    pub settled: usize,
    pub running: usize,
}

/// `MightyTimeline.ExecutionKind`: the kind of the 실행 row.
pub const EXECUTION_KIND: &str = "execution";
/// `MightyTimeline.RequestStatus`: the request row's status, neither running nor settled.
pub const REQUEST_STATUS: &str = "idle";
/// `MightyTimeline.RequestNode`.
pub const REQUEST_NODE: Node = Node { tone: Tone::Idle, ring: false, rail: None };

/// `MightyTimeline.Result(NodeId, Tone, Text, Awaiting)`: a request's result head. `awaiting`: the request awaits the notices of its
/// background work, and the head reads 알림 대기 중 in the wait tone.
#[derive(Debug, Clone, PartialEq)]
pub struct TimelineResult {
    pub node_id: String,
    pub tone: Tone,
    pub text: Option<String>,
    pub awaiting: bool,
}

/// `MightyTimeline.Group(RunIndex, RunId, Ordinal, Status, Input, Rows, Tally, Result, NoticeRun, NoticeTitle)`: one request's group.
#[derive(Debug, Clone, PartialEq)]
pub struct Group {
    pub run_index: usize,
    pub run_id: String,
    pub ordinal: i32,
    pub status: String,
    pub input: String,
    pub rows: Vec<Row>,
    pub tally: Tally,
    pub result: Option<TimelineResult>,
    pub notice_run: bool,
    pub notice_title: Option<String>,
}

/// `MightyTimeline.NormalizeMode` (MightyTimeline.cs:31): the diagram, the timeline or the structure; a word this build does not know
/// reads as none (the diagram).
pub fn normalize_mode(value: Option<&str>) -> Option<&str> {
    value.filter(|v| matches!(*v, "diagram" | "timeline" | "structure"))
}

/// `MightyTimeline.Mode`.
pub fn mode(session: &RunSession) -> &str {
    normalize_mode(session.graph_view_mode.as_deref()).unwrap_or("diagram")
}

/// `MightyGraphViewModel.SupportsMighty`: an agent pane of a provider whose runs make a graph.
fn supports_mighty(session: &RunSession) -> bool {
    session.kind() == "claude" && GRAPH_PROVIDERS.contains(&session.provider())
}

/// `MightyTimeline.ApplyMode` (MightyTimeline.cs:33): an agent pane's presentation, only for a word this build knows.
pub fn apply_mode(session: &RunSession, value: &str) -> RunSession {
    let mut copy = session.clone();
    if supports_mighty(session)
        && let Some(mode) = normalize_mode(Some(value))
    {
        copy.graph_view_mode = Some(mode.to_string());
    }
    copy
}

/// `MightyTimeline.Status` (MightyTimeline.cs:38): the buckets the phone and the Mac share; unknown, queued and idle are unfinished work.
pub fn status(value: &str) -> &'static str {
    match value {
        "completed" => "completed",
        "error" | "failed" => "error",
        "stopped" | "cancelled" | "interrupted" => "stopped",
        "waiting" => "waiting",
        _ => "running",
    }
}

/// `MightyTimeline.NodeFor`.
pub fn node_for(value: &str) -> Node {
    let status = status(value);
    let tone = Tone::of(status);
    Node { tone, ring: status == "running", rail: matches!(status, "completed" | "running").then_some(tone) }
}

/// `MightyTimeline.RailAbove`: the rail the row before `index` lights.
pub fn rail_above(rows: &[Row], index: usize) -> Option<Tone> {
    (index > 0 && index < rows.len()).then(|| rows[index - 1].node.rail).flatten()
}

/// `MightyTimeline.GroupOpen` (MightyTimeline.cs:50): open by default the newest request and a notice run after it with a result;
/// `flipped` holds the groups the user toggled.
pub fn group_open(group: &Group, runs: &[MightyGraphRun], flipped: &HashSet<String>) -> bool {
    let index = group.run_index;
    let open = match support::newest_request_index(runs) {
        None => runs.len().checked_sub(1) == Some(index),
        Some(newest) => {
            index == newest || (index > newest && index < runs.len() && runs[index].is_notice() && support::has_result(&runs[index]))
        }
    };
    open != flipped.contains(&group.run_id)
}

/// `MightyTimeline.Folds`: a result past 600 characters (grapheme clusters) or 8 line feeds folds.
pub fn folds(text: &str) -> bool {
    text.graphemes(true).count() > 600 || text.matches('\n').count() >= 8
}

/// `MightyTimeline.Groups` (MightyTimeline.cs:60): every request the diagram draws, oldest first, numbered as the diagram numbers them.
/// The rows index the agents of the run as a view draws it (`support::shown_in`).
pub fn groups(runs: &[MightyGraphRun], locale: &Locale) -> Vec<Group> {
    let runs = support::with_background_waiting(runs);
    let ordinals = support::request_ordinals(&runs);
    runs.iter().enumerate().map(|(index, run)| project(&support::shown_in(run, &runs, locale), index, ordinals[index], locale)).collect()
}

fn project(run: &MightyGraphRun, index: usize, ordinal: i32, locale: &Locale) -> Group {
    let notice = run.is_notice();
    let mut rows = vec![];
    let add = |rows: &mut Vec<Row>,
               agent_index: Option<usize>,
               suffix: &str,
               kind: &str,
               title: String,
               raw: &str,
               entries: &[LogEntry],
               execution: bool| {
        let status = status(raw);
        rows.push(Row {
            node_id: support::node_id(run, suffix),
            agent_index,
            kind: kind.to_string(),
            title,
            status: status.to_string(),
            node: node_for(status),
            latest: if matches!(status, "running" | "waiting") { latest(entries) } else { None },
            duration_ms: duration(entries, status),
            is_execution: execution,
        });
    };
    // As in the diagram, the request holds its input alone, in a neutral state of its own, and a 실행 block after it holds the work.
    rows.push(Row {
        node_id: support::node_id(run, "request"),
        agent_index: None,
        kind: "main".into(),
        title: if notice {
            notice::heading(run, locale)
        } else {
            locale.get_with("graph.timeline.requestOrdinal", &[("n", &ordinal.to_string())])
        },
        status: REQUEST_STATUS.into(),
        node: REQUEST_NODE,
        latest: None,
        duration_ms: None,
        is_execution: false,
    });
    add(
        &mut rows,
        None,
        WORK_SUFFIX,
        EXECUTION_KIND,
        notice::execution_title(notice, ordinal, locale),
        run.status(),
        run.root_entries(),
        true,
    );
    for (i, agent) in run.agents().iter().enumerate() {
        add(
            &mut rows,
            Some(i),
            &format!("agent:{}", agent.id()),
            support::block_kind(agent),
            support::block_title(agent, locale),
            agent.status(),
            agent.entries(),
            false,
        );
    }
    let mut result = None;
    if support::has_result(run) {
        let tone = Tone::of(status(run.status()));
        // Awaiting notices, the head takes the wait tone, as the diagram's 알림 대기 block does; else the result so far is neutral.
        let awaiting = notice::awaiting_notice(run, std::slice::from_ref(run), locale).is_some();
        let tone = if awaiting {
            Tone::Wait
        } else if run.status() == "completed" && support::background_waiting(run) > 0 {
            Tone::Idle
        } else if tone == Tone::Run {
            Tone::Done
        } else {
            tone
        };
        result = Some(TimelineResult {
            node_id: support::node_id(run, "result"),
            tone,
            text: run.result_entries().iter().rev().find(|e| e.kind() == "assistant" && !e.text().is_empty()).map(|e| e.text().to_string()),
            awaiting,
        });
    }
    let blocks = &rows[1..];
    let tally = Tally {
        total: blocks.len(),
        settled: blocks.iter().filter(|r| matches!(r.status.as_str(), "completed" | "error" | "stopped")).count(),
        running: blocks.iter().filter(|r| r.status == "running").count(),
    };
    Group {
        run_index: index,
        run_id: run.id().to_string(),
        ordinal,
        status: status(run.status()).to_string(),
        input: run.input().to_string(),
        tally,
        rows,
        result,
        notice_run: notice,
        notice_title: if notice { run.notice_title.clone() } else { None },
    }
}

/// `MightyTimeline.Latest`: an unfinished block's latest line — its newest tool call (not the turn), else its newest reply's first line.
fn latest(entries: &[LogEntry]) -> Option<String> {
    for entry in entries.iter().rev() {
        let text = match &entry.activity {
            Some(activity) => {
                if activity.kind == "turn" {
                    continue;
                }
                if activity.summary.is_empty() { activity.tool_name.clone() } else { Some(activity.summary.clone()) }
            }
            None => {
                if !matches!(entry.kind(), "assistant" | "error") || crate::structure::is_questionnaire(entry.text()) {
                    continue;
                }
                Some(entry.text().trim_start().split(['\r', '\n']).next().unwrap_or("").to_string())
            }
        };
        let clean = activity_clean(text.as_deref(), 512, true);
        if !clean.is_empty() {
            return Some(clean);
        }
    }
    None
}

/// `ActivitySupport.MaximumDurationMs`: 30 days.
const MAXIMUM_DURATION_MS: f64 = 30.0 * 24.0 * 60.0 * 60.0 * 1000.0;

/// `ActivitySupport.ValidDuration`.
fn valid_duration(value: f64) -> bool {
    value.is_finite() && (0.0..=MAXIMUM_DURATION_MS).contains(&value)
}

/// The UTC ticks of a time `DateTimeOffset.TryParse(…, AssumeUniversal)` reads: a time with no zone is UTC.
fn universal_ticks(text: Option<&str>) -> Option<i64> {
    let parsed = time::parse(text)?;
    let text = text?.trim();
    let zoned = text.find(['T', 't', ' ']).is_some_and(|t| text[t..].contains(['Z', 'z', '+', '-']));
    Some(if zoned { parsed.utc_ticks() } else { parsed.local_ticks })
}

/// `MightyTimeline.Duration`: a settled block's own record span, rounded to the millisecond (ties to even, as `Math.Round`).
fn duration(entries: &[LogEntry], status: &str) -> Option<f64> {
    if !matches!(status, "completed" | "error" | "stopped") || entries.len() < 2 {
        return None;
    }
    let first = universal_ticks(entries[0].timestamp.as_deref())?;
    let last = universal_ticks(entries[entries.len() - 1].timestamp.as_deref())?;
    let milliseconds = (last - first) as f64 / 10_000.0;
    (milliseconds > 0.0 && valid_duration(milliseconds)).then(|| milliseconds.round_ties_even())
}

/// `MightyTimeline.KindLabel` (MightyTimeline.cs:134): `input` is what the block was asked; a steer delegation sent, or a task notice, is
/// named for that.
pub fn kind_label(kind: &str, input: Option<&str>, locale: &Locale) -> String {
    locale.get(match kind {
        "main" => "graph.timeline.kind.request",
        EXECUTION_KIND => "graph.block.execution",
        "task" => "graph.block.task",
        "steer" if support::is_delegation_notice(input) => "graph.block.delegationNotice",
        "steer" if support::is_task_notice(input) => "graph.block.taskNotice",
        "steer" => "graph.block.steer",
        "compact" => "graph.block.compact",
        "question" => "graph.block.question",
        _ => "graph.block.agent",
    })
}

/// `MightyTimeline.DurationLabel` (MightyTimeline.cs:140): "999ms", "12.3초", "2분", "2분 5초".
pub fn duration_label(milliseconds: Option<f64>, locale: &Locale) -> Option<String> {
    let ms = milliseconds.filter(|m| valid_duration(*m))?;
    if ms < 1000.0 {
        return Some(format!("{}ms", ms as i64));
    }
    if ms < 60_000.0 {
        return Some(locale.get_with("run.activity.durationSeconds", &[("seconds", &format!("{:.1}", (ms / 100.0).floor() / 10.0))]));
    }
    let seconds = (ms / 1000.0) as i64;
    let key = if seconds % 60 == 0 { "run.activity.durationMinutes" } else { "run.activity.durationMinutesSeconds" };
    Some(locale.get_with(key, &[("minutes", &(seconds / 60).to_string()), ("seconds", &(seconds % 60).to_string())]))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn durations_read_as_dotnet_words_them() {
        let ko = Locale::new("ko");
        assert_eq!(duration_label(Some(999.0), &ko).as_deref(), Some("999ms"));
        assert_eq!(duration_label(Some(12_345.0), &ko).as_deref(), Some("12.3초"));
        assert_eq!(duration_label(Some(120_000.0), &ko).as_deref(), Some("2분"));
        assert_eq!(duration_label(Some(125_000.0), &ko).as_deref(), Some("2분 5초"));
        assert_eq!(
            universal_ticks(Some("2026-10-04T10:00:01.234Z")).unwrap() - universal_ticks(Some("2026-10-04T10:00:00Z")).unwrap(),
            12_340_000
        );
    }
}
