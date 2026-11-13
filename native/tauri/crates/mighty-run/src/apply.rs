//! A run's events applied to its pane: C# `RunSession.Apply` and `AppSnapshot.Apply` (Models.cs:189-247, 420), with the parts of
//! `MightyGraphSupport` they read (`RoutedIndex`, `MergeRootEntries`, `BoundedLiveHistory`, `NoticeTurnRunning`/`Ended`;
//! MightyGraph.cs) and `TranscriptRetention`.

use std::sync::Arc;

use mighty_core::graph::{GRAPH_PROVIDERS, MightyGraphRun, terminal};
use mighty_core::model::{AppSnapshot, LogEntry, RunSession, SessionUsage};
use mighty_core::normalize::{self, LIVE_HISTORY_LIMIT};
use mighty_core::time::DateTimeOffset;
use mighty_core::wire;

use crate::flow_steps;
use crate::model::RunEvent;
use crate::plan_mode;
use crate::resume;
use crate::timing;

/// `TranscriptRetention.MaximumEntries`.
pub const MAXIMUM_ENTRIES: usize = 400;
/// `MightyGraphSupport.MaximumRootEntries`.
pub const MAXIMUM_ROOT_ENTRIES: usize = 100;

/// `TranscriptRetention.Trimmed`.
pub fn trimmed(entries: Vec<LogEntry>) -> Vec<LogEntry> {
    let skip = entries.len().saturating_sub(MAXIMUM_ENTRIES);
    entries.into_iter().skip(skip).collect()
}

/// `MightyGraphSupport.NewestRequestIndex`.
pub fn newest_request_index(runs: &[MightyGraphRun]) -> Option<usize> {
    runs.iter().rposition(|run| !run.is_notice())
}

/// `MightyGraphSupport.NoticeTurnRunning`: a notice run after the newest request has not ended.
pub fn notice_turn_running(runs: &[MightyGraphRun]) -> bool {
    let newest = newest_request_index(runs);
    runs.iter()
        .enumerate()
        .rev()
        .take_while(|(index, _)| newest.is_none_or(|n| *index > n))
        .any(|(_, run)| run.is_notice() && !terminal(run.status()))
}

/// `MightyGraphSupport.NoticeTurnEnded`.
pub fn notice_turn_ended(before: &[MightyGraphRun], after: &[MightyGraphRun]) -> bool {
    notice_turn_running(before) && !notice_turn_running(after)
}

/// `MightyGraphSupport.RoutedIndex`: the run the process's lines belong to; None without runs.
pub fn routed_index(runs: &[MightyGraphRun]) -> Option<usize> {
    if runs.is_empty() {
        return None;
    }
    let mut index = runs.len() as isize - 1;
    while index >= 0 && runs[index as usize].is_notice() && terminal(runs[index as usize].status()) {
        index -= 1;
    }
    if index < 0 {
        index = runs.len() as isize - 1;
    }
    let at = index as usize;
    if runs[at].source_run_id.is_none() && !runs[at].is_notice() {
        for notice in (0..runs.len()).rev() {
            if runs[notice].is_notice() && !terminal(runs[notice].status()) {
                return Some(notice);
            }
        }
    }
    Some(at)
}

/// `MightyGraphSupport.MergeRootEntries`: `incoming` laid over `earlier` by id, the request left out, a result entry the transcript
/// already holds as an answer dropped, the newest 100 kept.
pub fn merge_root_entries(earlier: &[LogEntry], incoming: &[LogEntry]) -> Vec<LogEntry> {
    let mut merged: Vec<LogEntry> = earlier.iter().filter(|e| e.kind() != "user").cloned().collect();
    for entry in incoming.iter().filter(|e| e.kind() != "user") {
        match merged.iter().position(|e| e.id == entry.id) {
            Some(index) => merged[index] = entry.clone(),
            None => merged.push(entry.clone()),
        }
    }
    let remove: Vec<bool> = merged
        .iter()
        .enumerate()
        .map(|(index, e)| {
            e.id().ends_with("-result")
                && merged.iter().enumerate().any(|(other, o)| other != index && o.kind() == "assistant" && o.text == e.text)
        })
        .collect();
    let kept: Vec<LogEntry> = merged.into_iter().zip(remove).filter(|(_, gone)| !gone).map(|(e, _)| e).collect();
    let skip = kept.len().saturating_sub(MAXIMUM_ROOT_ENTRIES);
    kept.into_iter().skip(skip).collect()
}

fn bytes(value: Option<&str>) -> i64 {
    value.map_or(0, |v| v.len() as i64)
}

fn entry_bytes(entries: &[LogEntry]) -> i64 {
    entries
        .iter()
        .map(|e| {
            512 + bytes(e.text.as_deref())
                + bytes(e.activity.as_ref().and_then(|a| a.output.as_deref()))
                + bytes(e.activity.as_ref().map(|a| a.summary.as_str()))
                + normalize::approximate_bytes(e.images.as_ref())
        })
        .sum()
}

/// `MightyGraphSupport.LiveBytes`.
fn live_bytes(run: &MightyGraphRun) -> i64 {
    512 + bytes(run.id.as_deref())
        + bytes(run.source_run_id.as_deref())
        + bytes(run.input.as_deref())
        + bytes(run.final_output.as_deref()) * 3
        + entry_bytes(run.root_entries())
        + run
            .agents()
            .iter()
            .map(|a| {
                768 + bytes(a.id.as_deref())
                    + bytes(a.parent_id.as_deref())
                    + bytes(a.title.as_deref())
                    + bytes(a.input.as_deref())
                    + entry_bytes(a.entries())
            })
            .sum::<i64>()
}

/// `MightyGraphSupport.BoundedLiveHistory`: the oldest runs let go down to three quarters of 2 MiB, then the rest bounded.
pub fn bounded_live_history(values: Vec<MightyGraphRun>) -> Vec<MightyGraphRun> {
    let mut sizes: Vec<i64> = values.iter().map(live_bytes).collect();
    let mut total: i64 = sizes.iter().sum();
    if total <= LIVE_HISTORY_LIMIT {
        return values;
    }
    let low_water = LIVE_HISTORY_LIMIT * 3 / 4;
    let mut kept = values;
    while total > low_water && kept.len() > 1 {
        total -= sizes.remove(0);
        kept.remove(0);
    }
    if total <= LIVE_HISTORY_LIMIT {
        return kept;
    }
    let mut budget = LIVE_HISTORY_LIMIT;
    normalize::graph_runs(&kept, false, &mut budget, None).unwrap_or(kept)
}

/// `RunSession.Apply`.
pub fn apply_session(session: &RunSession, event: &RunEvent, now: DateTimeOffset) -> RunSession {
    // A checklist change goes on the request's steps before the pane's list is replaced with it (FlowSteps).
    let mut value = plan_mode::apply(flow_steps::record_steps(session.clone(), event, now), event);
    let mut timing = session.run_timing;
    if session.kind() != "shell" {
        let status = event.status.as_deref();
        if event.kind == "status" && status == Some("running") {
            timing = Some(match timing {
                Some(t) if t.finished_at.is_none() => timing::observe(t, Some(now)),
                _ => timing::begin(Some(now)),
            });
        } else if event.kind == "status" && matches!(status, Some("completed" | "error" | "stopped")) {
            timing = timing.map(|t| timing::finish(t, Some(now)));
        } else if session.status() == "running" {
            timing = timing.map(|t| timing::observe(t, Some(now)));
        }
    }
    value.run_timing = timing;
    match event.kind.as_str() {
        "status" => {
            let status = event.status.clone().unwrap_or_default();
            let ended = matches!(status.as_str(), "completed" | "error" | "stopped");
            if status == "error" && resume::lost(&value) {
                value.resume_id = None;
            }
            if ended {
                value.current_activity = None;
                let settled = if status == "stopped" { "stopped" } else { "error" };
                value.logs = Some(
                    value
                        .logs()
                        .iter()
                        .map(|log| match &log.activity {
                            Some(a) if matches!(a.state.as_str(), "running" | "waiting") => {
                                let mut log = log.clone();
                                if let Some(activity) = log.activity.as_mut() {
                                    activity.state = settled.into();
                                }
                                log
                            }
                            _ => log.clone(),
                        })
                        .collect(),
                );
            }
            value.status = Some(status);
            value
        }
        "resume" => RunSession { resume_id: event.resume_id.clone(), ..value },
        "usage" => {
            let usage = event
                .usage
                .as_ref()
                .filter(|u| session.kind() == "claude" && u.provider == session.provider())
                .and_then(normalize::session_usage);
            match usage {
                Some(usage) => {
                    let selected = wire::model(session.model()).then(|| session.model().to_string());
                    RunSession { session_usage: Some(SessionUsage { selected_model: selected, ..usage }), ..value }
                }
                None => value,
            }
        }
        "activity" => match event.activity.as_ref().filter(|a| a.provider == session.provider()) {
            Some(activity) => RunSession { current_activity: normalize::activity(activity, false), ..value },
            None => value,
        },
        "log" => {
            let Some(entry) = &event.entry else { return value };
            let mut logs = value.logs.clone().unwrap_or_default();
            match logs.iter().position(|l| l.id == entry.id) {
                Some(index) => logs[index] = entry.clone(),
                None => logs.push(entry.clone()),
            }
            value.logs = Some(trimmed(logs));
            // The request still running also shows the main agent's own work as it comes.
            if entry.kind() != "user"
                && session.kind() == "claude"
                && GRAPH_PROVIDERS.contains(&session.provider())
                && let Some(live) = value.graph_runs.as_ref().filter(|r| !r.is_empty())
                && let Some(at) = routed_index(live)
            {
                let run = &live[at];
                let turn_ended = value.background_work.as_ref().is_some_and(|w| w.turn_ended);
                if !terminal(run.status()) || !run.is_notice() && value.status() == "running" && turn_ended {
                    let mut runs = live.clone();
                    runs[at].root_entries = Some(merge_root_entries(runs[at].root_entries(), std::slice::from_ref(entry)));
                    value.graph_runs = Some(runs);
                }
            }
            value
        }
        "graph_run" => {
            let Some(new_run) =
                event.graph_run.as_ref().filter(|_| session.kind() == "claude" && GRAPH_PROVIDERS.contains(&session.provider()))
            else {
                return value;
            };
            let mut runs: Vec<MightyGraphRun> = session.graph_runs.clone().unwrap_or_default();
            let at = runs.iter().position(|r| r.id == new_run.id);
            let shown: Vec<LogEntry> = match at {
                Some(at) => runs[at].root_entries().to_vec(),
                None if new_run.is_notice() => vec![],
                None => {
                    let logs = value.logs();
                    let start = logs.iter().rposition(|l| l.kind() == "user").map(|i| i + 1).unwrap_or(0);
                    logs[start..].to_vec()
                }
            };
            let mut run = new_run.clone();
            run.root_entries = Some(merge_root_entries(&shown, new_run.root_entries()));
            match at {
                Some(at) => {
                    if run.steps.is_none() {
                        run.steps = runs[at].steps.clone();
                    }
                    if run.steps_carried.is_none() {
                        run.steps_carried = runs[at].steps_carried.clone();
                    }
                    runs[at] = run;
                }
                None => runs.push(run),
            }
            let skip = runs.len().saturating_sub(128);
            let bounded = bounded_live_history(runs.into_iter().skip(skip).collect());
            RunSession { graph_runs: (!bounded.is_empty()).then_some(bounded), ..value }
        }
        _ => value,
    }
}

/// `AppSnapshot.Apply`: a valid event applied to its pane.
pub fn apply(snapshot: &AppSnapshot, event: &RunEvent, now: DateTimeOffset) -> AppSnapshot {
    if !event.valid() {
        return snapshot.clone();
    }
    let mut next = snapshot.clone();
    for session in next.sessions_mut().iter_mut() {
        if session.id() == event.session_id {
            *session = Arc::new(apply_session(session, event, now));
        }
    }
    next
}
