//! The running request shows the main agent's work (C# MightyGraphLiveVerification, MightyCode.Core.Tests/MightyGraphLiveVerification.cs).
//! The pane's own record of run events (`RunSession.Apply(RunEvent)`) is the run engine's; this keeps the graph's half, the root entries'
//! merge (`MightyGraphSupport.MergeRootEntries`) and the result refresh it relies on.

mod view_support;

use mighty_graph::support;
use view_support::*;

/// "mighty graph: the running request shows the main agent's work" (TheRunningRequestShowsTheMainAgentsWork): the merge's rules.
#[test]
fn the_running_request_shows_the_main_agents_work() {
    let log = |id: &str, kind: &str, text: &str| entry(id, kind, text, "2026-10-23T00:00:00Z", Some("gemini"));
    let tool =
        |state: &str| with_activity(log("tool-1", "system", "README.md"), activity("tool-1", "gemini", "read", state, "README.md", None));
    // The first graph: the transcript's tool row since the request was asked, the request itself left out.
    let first = support::merge_root_entries(&[], &[log("ask", "user", "why"), tool("running")]);
    check(
        first.iter().map(|e| e.id()).collect::<Vec<_>>() == ["tool-1"],
        "a request seen first starts with the transcript since it was asked, not before",
    );
    // Later rows are added and an updated row replaces its earlier state.
    let later = support::merge_root_entries(&first, &[tool("completed"), log("answer", "assistant", "because")]);
    check(
        later.len() == 2 && later[0].activity.as_ref().unwrap().state == "completed" && later[1].text() == "because",
        "later rows are added and an updated row replaces its earlier state",
    );
    // The pipeline's next graph has no transcript; what the request showed stays, and the result is not said twice.
    let mut fin = run("run", "why", "completed");
    fin.provider = Some("gemini".into());
    fin.final_output = Some("because".into());
    support::refresh_result(&mut fin);
    let merged = support::merge_root_entries(&later, fin.root_entries());
    check(
        merged.iter().map(|e| e.id()).collect::<Vec<_>>() == ["tool-1", "answer"],
        "a later graph keeps the shown work and drops the duplicate result entry",
    );
    check(fin.result_entries().len() == 1 && fin.result_entries()[0].text() == "because", "the result block still has the answer");
    let many: Vec<_> = (0..150).map(|i| log(&format!("e{i}"), "system", "x")).collect();
    check(support::merge_root_entries(&[], &many).len() == support::MAXIMUM_ROOT_ENTRIES, "the newest 100 entries are kept");
}

/// C# TheRunningRequestShowsTheMainAgentsWork (MightyGraphLiveVerification.cs:9-36) through the pane's own record of the run events (the
/// run engine's `RunSession.Apply`): the transcript's rows join the running request, a later graph keeps them, a finished one takes none.
#[test]
fn the_running_request_shows_the_main_agents_work_on_the_pane() {
    use std::sync::Arc;

    use mighty_core::AppSnapshot;
    use mighty_core::model::RunSession;
    use mighty_run::RunEvent;

    let log = |id: &str, kind: &str, text: &str| entry(id, kind, text, "2026-10-23T00:00:00Z", Some("gemini"));
    let tool =
        |state: &str| with_activity(log("tool-1", "system", "README.md"), activity("tool-1", "gemini", "read", state, "README.md", None));
    let apply = |pane: RunSession, value: RunEvent| -> RunSession {
        let state = AppSnapshot { sessions: Some(vec![Arc::new(pane)]), ..AppSnapshot::default() };
        (*mighty_run::apply::apply(&state, &value, mighty_core::time::DateTimeOffset::now()).sessions()[0]).clone()
    };
    let logged = |e| RunEvent { entry: Some(e), ..RunEvent::new("pane", "log") };
    let graphed = |r| RunEvent { graph_run: Some(r), ..RunEvent::new("pane", "graph_run") };
    let mut pane = RunSession {
        id: Some("pane".into()),
        kind: Some("claude".into()),
        provider: Some("gemini".into()),
        status: Some("running".into()),
        logs: Some(vec![log("old", "assistant", "earlier answer"), log("ask", "user", "why")]),
        ..RunSession::default()
    };
    // Before the pipeline's first graph: the transcript already has a tool row for this request.
    pane = apply(pane, logged(tool("running")));
    let mut asked = run("run", "why", "running");
    asked.provider = Some("gemini".into());
    pane = apply(pane, graphed(asked.clone()));
    check(
        pane.graph_runs().last().unwrap().root_entries().iter().map(|e| e.id()).collect::<Vec<_>>() == ["tool-1"],
        "a request seen first starts with the transcript since it was asked, not before",
    );
    pane = apply(pane, logged(tool("completed")));
    pane = apply(pane, logged(log("answer", "assistant", "because")));
    let shown = pane.graph_runs().last().unwrap().root_entries().to_vec();
    check(
        shown.len() == 2 && shown[0].activity.as_ref().unwrap().state == "completed" && shown[1].text() == "because",
        "later rows are added and an updated row replaces its earlier state",
    );
    // The pipeline's next graph has no transcript; what the request showed stays, and the result is not said twice.
    let mut fin = run("run", "why", "completed");
    fin.provider = Some("gemini".into());
    fin.final_output = Some("because".into());
    support::refresh_result(&mut fin);
    pane = apply(pane, graphed(fin));
    let last = pane.graph_runs().last().unwrap().clone();
    check(
        last.root_entries().iter().map(|e| e.id()).collect::<Vec<_>>() == ["tool-1", "answer"],
        "a later graph keeps the shown work and drops the duplicate result entry",
    );
    check(last.result_entries().len() == 1 && last.result_entries()[0].text() == "because", "the result block still has the answer");
    // A finished request takes no more rows.
    pane = apply(pane, logged(log("late", "system", "after")));
    check(pane.graph_runs().last().unwrap().root_entries().iter().all(|e| e.id() != "late"), "a finished request takes no more rows");
}
