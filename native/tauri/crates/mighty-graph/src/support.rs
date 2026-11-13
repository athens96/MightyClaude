//! The rules every Mighty view reads a pane's graph runs by (C# `MightyGraphSupport`, MightyCode.Core/MightyGraph.cs:171, and the
//! computed members of `MightyGraphRun` and `MightyGraphAgent`, MightyGraph.cs:33-168). The saved records and the normalize live in
//! `mighty_core::graph` and `mighty_core::normalize`; this adds what the views and the tracker read of them.

use std::borrow::Cow;
use std::collections::HashSet;

use mighty_core::graph::{GraphTokenUsage, MightyGraphAgent, MightyGraphRun, shown_agents};
use mighty_core::locale::Locale;
use mighty_core::model::{LogEntry, RunSession};
use mighty_core::normalize;

use crate::labels;
use crate::node;
use crate::notice;

/// `MightyGraphSupport.Providers`.
pub use mighty_core::graph::GRAPH_PROVIDERS as PROVIDERS;
/// `MightyGraphSupport.Terminal`.
pub use mighty_core::graph::terminal;
/// `MightyGraphSupport.ValidEffort` (a word that is no effort is none).
pub fn valid_effort(value: Option<&str>) -> bool {
    value.is_some_and(normalize::valid_effort)
}

/// `MightyGraphSupport.BlockKind`.
pub fn block_kind(agent: &MightyGraphAgent) -> &str {
    match agent.kind() {
        Some(kind @ ("task" | "steer" | "compact" | "question")) => kind,
        _ => "agent",
    }
}

/// `MightyGraphAgent.IsSubagent`: a block that is none of the kinds the core records apart.
pub fn is_subagent(agent: &MightyGraphAgent) -> bool {
    block_kind(agent) == "agent"
}

/// `MightyGraphSupport.BlockTitle`.
pub fn block_title(agent: &MightyGraphAgent, locale: &Locale) -> String {
    let title = agent.title();
    match block_kind(agent) {
        "steer" => {
            if is_delegation_notice(Some(agent.input())) {
                locale.get("graph.block.delegationNotice")
            } else if is_task_notice(Some(agent.input())) {
                locale.get("graph.block.taskNotice")
            } else {
                locale.get("graph.block.steer")
            }
        }
        "compact" => labels::compaction_title(locale),
        "question" if title.is_empty() => locale.get("graph.block.question"),
        "task" if title.is_empty() => locale.get("graph.block.task"),
        "agent" if title.is_empty() => locale.get("graph.block.agent"),
        _ => title.to_string(),
    }
}

/// `MightyGraphSupport.DelegationNoticePrefixes`.
pub const DELEGATION_NOTICE_PREFIXES: [&str; 4] =
    ["[Mighty Code notice ", "[Mighty Code follow-up ", "[Mighty Claude notice ", "[Mighty Claude follow-up "];

/// `MightyGraphSupport.IsDelegationNotice(string?)`.
pub fn is_delegation_notice(input: Option<&str>) -> bool {
    input.is_some_and(|i| DELEGATION_NOTICE_PREFIXES.iter().any(|p| i.starts_with(p)))
}

/// `MightyGraphSupport.TaskNoticePrefix`.
pub const TASK_NOTICE_PREFIX: &str = "[Mighty Code task notice] ";
const LEGACY_TASK_NOTICE_PREFIX: &str = "[Mighty Claude task notice] ";

fn task_notice_prefix_of(input: Option<&str>) -> Option<&'static str> {
    let input = input?;
    if input.starts_with(TASK_NOTICE_PREFIX) {
        Some(TASK_NOTICE_PREFIX)
    } else if input.starts_with(LEGACY_TASK_NOTICE_PREFIX) {
        Some(LEGACY_TASK_NOTICE_PREFIX)
    } else {
        None
    }
}

/// `MightyGraphSupport.IsTaskNotice(string?)`.
pub fn is_task_notice(input: Option<&str>) -> bool {
    task_notice_prefix_of(input).is_some()
}

/// `MightyGraphSupport.IsDelegationNotice(MightyGraphAgent)`: a steer that delegation sent.
pub fn is_delegation_notice_block(agent: &MightyGraphAgent) -> bool {
    block_kind(agent) == "steer" && is_delegation_notice(Some(agent.input()))
}

/// `MightyGraphSupport.IsTaskNotice(MightyGraphAgent)`: a steer the tracker made from a task notification.
pub fn is_task_notice_block(agent: &MightyGraphAgent) -> bool {
    block_kind(agent) == "steer" && is_task_notice(Some(agent.input()))
}

/// `MightyGraphSupport.IsNotice`: a steer no person sent.
pub fn is_notice(agent: &MightyGraphAgent) -> bool {
    is_delegation_notice_block(agent) || is_task_notice_block(agent)
}

/// `MightyGraphSupport.ShownInput`: a task notice without its prefix.
pub fn shown_input(agent: &MightyGraphAgent) -> &str {
    let input = agent.input();
    if is_task_notice_block(agent) { &input[task_notice_prefix_of(Some(input)).map_or(0, str::len)..] } else { input }
}

/// `MightyGraphSupport.NoticeSectionMark`.
pub const NOTICE_SECTION_MARK: &str = "\n\n---\n\n**";

/// `MightyGraphSupport.WithNoticeSection`: the result with a notice turn's answer after it, under a bold line naming the notice.
pub fn with_notice_section(result: Option<&str>, header: &str, answer: &str) -> String {
    let start = match result {
        Some(r) if !r.is_empty() => format!("{r}{NOTICE_SECTION_MARK}"),
        _ => "**".to_string(),
    };
    format!("{start}{header}**\n\n{answer}")
}

/// `MightyGraphSupport.Held`: the unfinished task and background-agent blocks a run holds itself.
pub fn held(run: &MightyGraphRun) -> i32 {
    run.shown_background_waiting.unwrap_or_else(|| unfinished(run))
}

fn unfinished(run: &MightyGraphRun) -> i32 {
    run.agents().iter().filter(|a| a.source_block_id.is_none() && (a.is_task() || a.is_background_agent()) && !terminal(a.status())).count()
        as i32
}

/// `MightyGraphSupport.BackgroundWaiting`: background work still running after the request's own turn ended.
pub fn background_waiting(run: &MightyGraphRun) -> i32 {
    if run.status() == "completed" { run.request_background_waiting.unwrap_or_else(|| held(run)) } else { 0 }
}

/// `MightyGraphSupport.WithBackgroundWaiting`: each completed run's count over its whole request (the request run and its notice runs).
pub fn with_background_waiting(runs: &[MightyGraphRun]) -> Cow<'_, [MightyGraphRun]> {
    if !runs.iter().any(MightyGraphRun::is_notice)
        || !runs.iter().any(|r| r.agents().iter().any(|a| a.is_task() || a.is_background_agent()))
    {
        return Cow::Borrowed(runs);
    }
    let mut counted = Vec::with_capacity(runs.len());
    let mut start = 0;
    while start < runs.len() {
        let mut end = start + 1;
        while end < runs.len() && runs[end].is_notice() {
            end += 1;
        }
        let total: i32 = runs[start..end].iter().map(held).sum();
        for run in &runs[start..end] {
            let waiting = if run.status() == "completed" { total } else { 0 };
            let mut copy = run.clone();
            if background_waiting(run) != waiting {
                copy.request_background_waiting = Some(waiting);
            }
            counted.push(copy);
        }
        start = end;
    }
    Cow::Owned(counted)
}

/// `MightyGraphSupport.RequestRuns`: every run but the notice runs.
pub fn request_runs(runs: &[MightyGraphRun]) -> Vec<MightyGraphRun> {
    runs.iter().filter(|r| !r.is_notice()).cloned().collect()
}

/// `MightyGraphSupport.NewestRequestIndex`.
pub fn newest_request_index(runs: &[MightyGraphRun]) -> Option<usize> {
    runs.iter().rposition(|r| !r.is_notice())
}

/// `MightyGraphSupport.NewestRequestRun`.
pub fn newest_request_run(runs: &[MightyGraphRun]) -> Option<&MightyGraphRun> {
    newest_request_index(runs).map(|i| &runs[i])
}

/// `MightyGraphSupport.NoticeTurnRunning`: a notice run after the newest request has not ended.
pub fn notice_turn_running(runs: &[MightyGraphRun]) -> bool {
    let after = newest_request_index(runs).map_or(0, |n| n + 1);
    runs[after..].iter().any(|r| r.is_notice() && !terminal(r.status()))
}

/// `MightyGraphSupport.NoticeTurnEnded`.
pub fn notice_turn_ended(before: &[MightyGraphRun], after: &[MightyGraphRun]) -> bool {
    notice_turn_running(before) && !notice_turn_running(after)
}

/// `MightyGraphRun.QuietNotice`: a notice run taken into a running turn, complete at once with no reply of its own.
pub fn quiet_notice(run: &MightyGraphRun) -> bool {
    run.is_notice() && run.status() == "completed" && run.final_output().is_none_or(|f| f.trim().is_empty())
}

/// `MightyGraphSupport.FollowRunIndex`: the newest request, or a notice run after it that is a turn of its own.
pub fn follow_run_index(runs: &[MightyGraphRun]) -> Option<usize> {
    let newest = newest_request_index(runs).map(|n| n as isize).unwrap_or(-1);
    (0..runs.len()).rev().find(|&i| i as isize == newest || (runs[i].is_notice() && i as isize > newest && !quiet_notice(&runs[i])))
}

/// `MightyGraphSupport.RequestOrdinals`: each run's request number, at least 1; notice runs carry the number before them.
pub fn request_ordinals(runs: &[MightyGraphRun]) -> Vec<i32> {
    let mut count = 0;
    runs.iter()
        .map(|r| {
            if !r.is_notice() {
                count += 1;
            }
            count.max(1)
        })
        .collect()
}

/// `MightyGraphSupport.WithoutNoticeRuns`: the pane as readers of "the user's requests" see it.
pub fn without_notice_runs(session: &RunSession) -> Cow<'_, RunSession> {
    match &session.graph_runs {
        Some(runs) if runs.iter().any(MightyGraphRun::is_notice) => {
            let mut copy = session.clone();
            copy.graph_runs = Some(request_runs(runs));
            Cow::Owned(copy)
        }
        _ => Cow::Borrowed(session),
    }
}

/// `MightyGraphRun.Shown`: the run as views draw it, background blocks only inside a notice run. The run itself (a copy) when nothing is
/// hidden; otherwise it carries what it waited on and what only the flow draws.
pub fn shown(run: &MightyGraphRun, locale: &Locale) -> MightyGraphRun {
    shown_in(run, std::slice::from_ref(run), locale)
}

/// `MightyGraphRun.ShownIn`: `Shown`, awaiting notices as the pane's `runs` (its notice runs among them) say it does.
pub fn shown_in(run: &MightyGraphRun, runs: &[MightyGraphRun], locale: &Locale) -> MightyGraphRun {
    let agents: Vec<MightyGraphAgent> = shown_agents(run).into_iter().cloned().collect();
    if agents.len() == run.agents().len() {
        return run.clone();
    }
    let mut copy = run.clone();
    copy.shown_background_waiting = Some(held(run));
    copy.shown_awaiting = notice::awaiting_notice(run, runs, locale);
    let fan_out = notice::fan_out(run);
    let kept: HashSet<&str> = agents.iter().map(|a| a.id()).collect();
    copy.shown_fan_out = Some(fan_out.iter().map(|&i| run.agents()[i].clone()).collect());
    let mut order: Vec<usize> = (0..run.agents().len()).filter(|&i| kept.contains(run.agents()[i].id())).collect();
    order.extend(fan_out.iter().copied());
    copy.shown_flow_order = Some(order);
    copy.agents = Some(agents);
    copy
}

/// `MightyGraphSupport.ShownRuns`: the runs as views draw them; the same list when no run holds a background block.
pub fn shown_runs<'a>(runs: &'a [MightyGraphRun], locale: &Locale) -> Cow<'a, [MightyGraphRun]> {
    let counted = with_background_waiting(runs);
    if counted.iter().any(|r| r.agents().iter().any(|a| a.background == Some(true))) {
        let list: &[MightyGraphRun] = &counted;
        Cow::Owned(list.iter().map(|r| shown_in(r, list, locale)).collect())
    } else {
        counted
    }
}

/// `MightyGraphRun.FlowAgents`: every block the flow diagram draws for the run, the shown agents then the fanned-out ones.
pub fn flow_agents(run: &MightyGraphRun) -> Vec<&MightyGraphAgent> {
    run.agents().iter().chain(run.shown_fan_out.iter().flatten()).collect()
}

/// `MightyGraphRun.FlowOrder(index)`: where `FlowAgents[index]` stood among the run's blocks as recorded.
pub fn flow_order(run: &MightyGraphRun, index: usize) -> usize {
    run.shown_flow_order.as_ref().and_then(|o| o.get(index).copied()).unwrap_or(index)
}

/// `MightyGraphRun.TotalUsage`: the main block's tokens and every child block's.
pub fn total_usage(run: &MightyGraphRun) -> Option<GraphTokenUsage> {
    let sum = run.agents().iter().fold(run.usage.unwrap_or_default(), |t, a| node::add(t, a.usage.unwrap_or_default()));
    (!node::is_empty(&sum)).then_some(sum)
}

/// `MightyGraphSupport.RoutedIndex`: the run the process's lines belong to; -1 without runs.
pub fn routed_index(runs: &[MightyGraphRun]) -> isize {
    let mut index = runs.len() as isize - 1;
    while index >= 0 && runs[index as usize].is_notice() && terminal(runs[index as usize].status()) {
        index -= 1;
    }
    if index < 0 {
        index = runs.len() as isize - 1;
    }
    if index >= 0 && runs[index as usize].source_run_id.is_none() && !runs[index as usize].is_notice() {
        for notice in (0..runs.len()).rev() {
            if runs[notice].is_notice() && !terminal(runs[notice].status()) {
                return notice as isize;
            }
        }
    }
    index
}

/// `MightyGraphSupport.BackgroundLabel(agent)`: the kind line under a background block's head; none for any other block.
pub fn background_label(agent: &MightyGraphAgent, locale: &Locale) -> Option<String> {
    (agent.is_task() || agent.is_background_agent()).then(|| background_label_of(agent.is_task(), agent.status(), locale))
}

/// `MightyGraphSupport.BackgroundLabel(run, status)`.
pub fn background_label_of(run: bool, status: &str, locale: &Locale) -> String {
    let word = match status {
        "completed" => Some(locale.get("graph.block.taskNotice.completed")),
        "error" | "failed" => Some(locale.get("graph.block.taskNotice.failed")),
        "stopped" | "cancelled" | "interrupted" => Some(locale.get("graph.block.taskNotice.stopped")),
        "unknown" => Some(locale.get("styles.state.taskStatus.unknown")),
        _ => None,
    };
    match word {
        None => locale.get(if run { "graph.background.runWaiting" } else { "graph.background.agentWaiting" }),
        Some(word) => locale.get_with(if run { "graph.background.runEnded" } else { "graph.background.agentEnded" }, &[("status", &word)]),
    }
}

/// `MightyGraphSupport.AgentNodeId`: the diagram's node id of a run's block.
pub fn agent_node_id(run_id: &str, agent_id: &str) -> String {
    mighty_core::graph::node_id(run_id, &format!("agent:{agent_id}"))
}

/// `MightyGraphSupport.NextState`.
pub fn next_state<'a>(previous: &'a str, incoming: &'a str) -> &'a str {
    if matches!(previous, "error" | "stopped") {
        return previous;
    }
    if matches!(incoming, "error" | "stopped") {
        return incoming;
    }
    if previous == "completed" {
        return previous;
    }
    incoming
}

/// `MightyGraphSupport.LiveHistoryLimit`.
pub const LIVE_HISTORY_LIMIT: i64 = normalize::LIVE_HISTORY_LIMIT;
const LIVE_HISTORY_LOW_WATER: i64 = LIVE_HISTORY_LIMIT * 3 / 4;

fn text_bytes(value: Option<&str>) -> i64 {
    value.map_or(0, |v| v.len() as i64)
}

fn entry_bytes(entries: &[LogEntry]) -> i64 {
    entries
        .iter()
        .map(|e| {
            512 + text_bytes(e.text.as_deref())
                + text_bytes(e.activity.as_ref().and_then(|a| a.output.as_deref()))
                + text_bytes(e.activity.as_ref().map(|a| a.summary.as_str()))
                + normalize::approximate_bytes(e.images.as_ref())
        })
        .sum()
}

fn live_bytes(run: &MightyGraphRun) -> i64 {
    512 + text_bytes(run.id.as_deref())
        + text_bytes(run.source_run_id.as_deref())
        + text_bytes(run.input.as_deref())
        + text_bytes(run.final_output.as_deref()) * 3
        + entry_bytes(run.root_entries())
        + run
            .agents()
            .iter()
            .map(|a| {
                768 + text_bytes(a.id.as_deref())
                    + text_bytes(a.parent_id.as_deref())
                    + text_bytes(a.title.as_deref())
                    + text_bytes(a.input.as_deref())
                    + entry_bytes(a.entries())
            })
            .sum::<i64>()
}

/// `MightyGraphSupport.LiveHistoryBytes`.
pub fn live_history_bytes(runs: &[MightyGraphRun]) -> i64 {
    runs.iter().map(live_bytes).sum()
}

/// `MightyGraphSupport.BoundedLiveHistory`: the oldest runs dropped to the low water mark, then the rest normalized under the limit.
pub fn bounded_live_history(values: &[MightyGraphRun]) -> Vec<MightyGraphRun> {
    let mut sizes: Vec<i64> = values.iter().map(live_bytes).collect();
    let mut total: i64 = sizes.iter().sum();
    if total <= LIVE_HISTORY_LIMIT {
        return values.to_vec();
    }
    let mut kept: Vec<MightyGraphRun> = values.to_vec();
    while total > LIVE_HISTORY_LOW_WATER && kept.len() > 1 {
        total -= sizes.remove(0);
        kept.remove(0);
    }
    if total <= LIVE_HISTORY_LIMIT {
        return kept;
    }
    let mut budget = LIVE_HISTORY_LIMIT;
    // A list the normalize refuses (a null it would throw on) cannot come from the tracker or a load; keep it as it is then.
    normalize::graph_runs(&kept, false, &mut budget, None).unwrap_or(kept)
}

/// `MightyGraphSupport.MaximumRootEntries`.
pub const MAXIMUM_ROOT_ENTRIES: usize = 100;

/// `MightyGraphSupport.MergeRootEntries`: `earlier` with `incoming` laid over them by id, the request left out, the newest 100 kept; the
/// result entry dropped when the transcript holds the same answer.
pub fn merge_root_entries(earlier: &[LogEntry], incoming: &[LogEntry]) -> Vec<LogEntry> {
    let mut merged: Vec<LogEntry> = earlier.iter().filter(|e| e.kind() != "user").cloned().collect();
    for entry in incoming.iter().filter(|e| e.kind() != "user") {
        match merged.iter().position(|e| e.id == entry.id) {
            Some(at) => merged[at] = entry.clone(),
            None => merged.push(entry.clone()),
        }
    }
    let drop: Vec<bool> = (0..merged.len())
        .map(|i| {
            let e = &merged[i];
            e.id().ends_with("-result") && merged.iter().enumerate().any(|(j, o)| j != i && o.kind() == "assistant" && o.text == e.text)
        })
        .collect();
    let mut index = 0;
    merged.retain(|_| {
        let keep = !drop[index];
        index += 1;
        keep
    });
    let start = merged.len().saturating_sub(MAXIMUM_ROOT_ENTRIES);
    merged.split_off(start)
}

/// `MightyGraphSupport.ApplyProvider`.
pub use mighty_core::normalize::apply_provider;
/// `MightyGraphSupport.RefreshResult`.
pub use mighty_core::normalize::refresh_result;

/// `MightyGraphSupport.Normalized(values, restoring, ref budget, provider)`.
pub fn normalized(values: &[MightyGraphRun], restoring: bool, budget: &mut i64, provider: Option<&str>) -> Vec<MightyGraphRun> {
    normalize::graph_runs(values, restoring, budget, provider).unwrap_or_default()
}

// ── what the layout says of a run, read by every view (C# `MightyGraphLayout` statics) ──────────────────────────────────────

/// `MightyGraphLayout.Terminal`: a block or task status that ended, in any agent's words.
pub fn layout_terminal(state: &str) -> bool {
    matches!(state, "completed" | "error" | "failed" | "stopped" | "cancelled" | "interrupted")
}

/// `MightyGraphLayout.Finished`: ended with every block a view draws for it.
pub fn finished(run: &MightyGraphRun) -> bool {
    layout_terminal(run.status()) && shown_agents(run).iter().all(|a| layout_terminal(a.status()))
}

/// `MightyGraphLayout.HasResult`: a result card under it.
pub fn has_result(run: &MightyGraphRun) -> bool {
    (finished(run) && !quiet_notice(run)) || run.reopened_with_reply()
}

/// `MightyGraphLayout.NodeID(run, suffix)`.
pub fn node_id(run: &MightyGraphRun, suffix: &str) -> String {
    mighty_core::graph::node_id(run.id(), suffix)
}

/// `MightyGraphLayout.LatestResultID`.
pub fn latest_result_id(runs: &[MightyGraphRun]) -> Option<String> {
    runs.iter().rev().find(|r| has_result(r)).map(|r| node_id(r, "result"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn notice_sections_follow_the_answer() {
        assert_eq!(with_notice_section(None, "작업 알림", "done"), "**작업 알림**\n\ndone");
        assert_eq!(with_notice_section(Some("first"), "x", "y"), "first\n\n---\n\n**x**\n\ny");
    }
}
