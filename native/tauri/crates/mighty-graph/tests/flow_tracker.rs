//! The role view's records as the graph tracker keeps them (C# FlowTrackerVerification.cs, the very cases of the macOS suite): who a
//! sub-agent block is, a foreground sub-agent's notification that opens no notice run, the tool calls a response record names over the
//! events of one message, a background command's start, end and exit code, and the advisor's consultations. The recordings are the shared
//! native/contracts/fixtures/claude-stream-*.jsonl: replayed here, they must give what they give on the Mac.

mod common;

use common::*;
use mighty_core::graph::{
    GraphAdvisor, GraphAdvisorCall, GraphAgentFigures, GraphAgentIdentity, GraphTokenUsage, MightyGraphAgent, MightyGraphRun,
};
use mighty_core::json::Wire;
use mighty_core::locale::Locale;
use mighty_core::normalize;
use mighty_core::time;
use mighty_graph::flow::roles::{self, FlowMark};
use mighty_graph::tracker::{self, ExecutionGraphTracker, ModGraphMetadata, ModMetadata};
use mighty_graph::{ExecutionGraphNode, node, notice, support, usage_format};
use serde_json::{Value, json};

fn identity(kind: Option<&str>, requested: Option<&str>, resolved: Option<&str>, isolation: Option<&str>) -> GraphAgentIdentity {
    GraphAgentIdentity {
        kind: kind.map(Into::into),
        requested_model: requested.map(Into::into),
        resolved_model: resolved.map(Into::into),
        isolation: isolation.map(Into::into),
    }
}

fn usage(input: i64, output: i64, cache_read: i64) -> GraphTokenUsage {
    GraphTokenUsage { input_tokens: input, output_tokens: output, cache_read_tokens: cache_read, cache_creation_tokens: 0 }
}

fn spawn(id: &str, input: Value) -> Value {
    json!({"type": "assistant", "uuid": format!("call-{id}"), "session_id": "main",
        "message": {"id": format!("call-{id}"), "content": [{"type": "tool_use", "id": id, "name": "Agent", "input": input}]}})
}

fn agent(pane: &Pane, tool: &str, run: &str) -> MightyGraphAgent {
    agent_of(&pane.runs(), run, tool)
}

fn result(text: &str, notice: bool, usage: Option<Value>) -> Value {
    let mut value = json!({"type": "result", "subtype": "success", "is_error": false, "result": text});
    if notice {
        value["origin"] = json!({"kind": "task-notification"});
    }
    if let Some(usage) = usage {
        value["usage"] = usage;
    }
    value
}

fn tool_result(tool: &str, text: &str, error: bool, structured: Option<Value>, key: &str) -> Value {
    let mut value =
        json!({"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": tool, "content": text, "is_error": error}]}});
    if let Some(structured) = structured {
        value[key] = structured;
    }
    value
}

fn notification(tool: &str, task: &str, status: &str, summary: &str) -> Value {
    json!({"type": "system", "subtype": "task_notification", "task_id": task, "tool_use_id": tool, "status": status, "summary": summary,
        "output_file": format!("/tmp/{task}.output")})
}

fn subagents(run: &MightyGraphRun) -> Vec<MightyGraphAgent> {
    run.agents().iter().filter(|a| support::is_subagent(a)).cloned().collect()
}

fn marks(agent: &MightyGraphAgent) -> Vec<FlowMark> {
    roles::subagent(agent, None, &ko()).expect("a sub-agent").marks
}

/// A run as C# `new MightyGraphRun { … }` makes one: its lists empty, running, no input.
fn graph_run(id: &str, status: &str) -> MightyGraphRun {
    MightyGraphRun {
        id: Some(id.into()),
        input: Some(String::new()),
        status: Some(status.into()),
        root_entries: Some(vec![]),
        agents: Some(vec![]),
        result_entries: Some(vec![]),
        ..MightyGraphRun::default()
    }
}

/// An agent as C# `new MightyGraphAgent { … }` makes one.
fn graph_agent(id: &str, title: &str, status: &str) -> MightyGraphAgent {
    MightyGraphAgent {
        id: Some(id.into()),
        title: Some(title.into()),
        input: Some(String::new()),
        status: Some(status.into()),
        entries: Some(vec![]),
        ..MightyGraphAgent::default()
    }
}

fn parse(text: &str) -> Value {
    serde_json::from_str(text).expect("json")
}

// ── Who a block is (GraphAgentIdentityTests) ──

#[test]
fn the_agent_call_the_launch_result_the_cli_and_mods_name_who_a_block_is() {
    let mut pane = Pane::new("run");
    pane.send(spawn(
        "a",
        json!({"subagent_type": "Explore", "model": "haiku", "isolation": "worktree", "description": "Map the repo", "prompt": "Look around", "run_in_background": true}),
    ));
    let block = agent(&pane, "a", "run");
    same(block.identity, Some(identity(Some("Explore"), Some("haiku"), None, Some("worktree"))), "the call's type, model and isolation");
    same(block.background, Some(true), "background");
    same(block.title.as_deref(), Some("Map the repo"), "the description names the block");
    pane.send(spawn("b", json!({"description": "Plain", "prompt": "Go"})));
    same(agent(&pane, "b", "run").identity, None, "a call that says nothing records nothing");
    pane.send(spawn("c", json!({"subagent_type": "fork", "prompt": "Go on"})));
    same(agent(&pane, "c", "run").title.as_deref(), Some("fork"), "a type alone titles the block");
    same(roles::is_fork(&agent(&pane, "c", "run")), true, "a fork");

    // The launch result names the model that runs; a foreground result the type and the worktree (the live key, then a transcript's).
    pane.send(spawn("bg", json!({"description": "Survey", "prompt": "Go", "model": "opus"})));
    pane.send(tool_result(
        "bg",
        "Async agent launched successfully.\nagentId: a1",
        false,
        Some(json!({"isAsync": true, "status": "async_launched", "agentId": "a1", "resolvedModel": "claude-opus-5-5"})),
        "tool_use_result",
    ));
    let background = agent(&pane, "bg", "run");
    same(background.identity.clone(), Some(identity(None, Some("opus"), Some("claude-opus-5-5"), None)), "the launch's resolved model");
    same(background.background, Some(true), "an asynchronous launch is a background agent");
    same(background.status(), "running", "still running");
    pane.send(spawn("fg", json!({"description": "Count", "prompt": "Go"})));
    pane.send(tool_result(
        "fg",
        "3",
        false,
        Some(json!({"status": "completed", "agentType": "general-purpose", "resolvedModel": "claude-haiku-5-5",
            "worktreePath": "/tmp/probe/.claude/worktrees/agent-1", "worktreeBranch": "worktree-agent-1"})),
        "toolUseResult",
    ));
    let foreground = agent(&pane, "fg", "run");
    same(
        foreground.identity.clone(),
        Some(identity(Some("general-purpose"), None, Some("claude-haiku-5-5"), Some("worktree"))),
        "a foreground result's type, model and worktree",
    );
    same(foreground.status(), "completed", "settled by its result");

    // task_started fills a type the call left unsaid and never replaces the call's own; a command's start is no agent's.
    pane.send(spawn("plain", json!({"description": "Plain", "prompt": "Go"})));
    pane.send(
        json!({"type": "system", "subtype": "task_started", "task_id": "t1", "tool_use_id": "plain", "subagent_type": "general-purpose",
        "is_backgrounded": false, "task_type": "local_agent"}),
    );
    same(agent(&pane, "plain", "run").identity, Some(identity(Some("general-purpose"), None, None, None)), "the CLI's type");
    pane.send(spawn("named", json!({"subagent_type": "Explore", "description": "Named", "prompt": "Go"})));
    pane.send(json!({"type": "system", "subtype": "task_started", "task_id": "t2", "tool_use_id": "named", "subagent_type": "other"}));
    same(agent(&pane, "named", "run").identity.and_then(|i| i.kind).as_deref(), Some("Explore"), "the call's type stays");
    pane.send(
        json!({"type": "assistant", "uuid": "sh", "message": {"id": "sh", "content": [{"type": "tool_use", "id": "shell", "name": "Bash",
        "input": {"command": "sleep 5", "run_in_background": true}}]}}),
    );
    pane.send(json!({"type": "system", "subtype": "task_started", "task_id": "t3", "tool_use_id": "shell", "subagent_type": "ghost", "task_type": "local_bash"}));
    same(agent(&pane, "shell", "run").identity, None, "a command has no identity");
    same(agent(&pane, "shell", "run").kind.as_deref(), Some("task"), "a command is a task block");

    // Mods: the type, the requested model while it starts, the resolved one once it runs; a failed start names none that ran.
    let mut mods = Pane::new("run");
    let modded = |phase: &str, model: Option<&str>| ModMetadata {
        claude_session_id: "main".into(),
        event: "agent.spawn".into(),
        graph: Some(ModGraphMetadata {
            version: 1,
            phase: phase.into(),
            agent_id: (phase != "starting").then(|| "agent-1".into()),
            parent_agent_id: None,
            parent_tool_use_id: Some("m".into()),
            name: Some("Review".into()),
            agent_type: Some("code-reviewer".into()),
            model: model.map(Into::into),
            input: Some("Review it".into()),
            output: None,
        }),
        ..ModMetadata::default()
    };
    mods.tracker.receive_mod(&modded("starting", Some("sonnet")));
    same(agent(&mods, "m", "run").identity, Some(identity(Some("code-reviewer"), Some("sonnet"), None, None)), "while it starts");
    mods.tracker.receive_mod(&modded("running", Some("claude-sonnet-5-5")));
    same(
        agent(&mods, "m", "run").identity,
        Some(identity(Some("code-reviewer"), Some("sonnet"), Some("claude-sonnet-5-5"), None)),
        "once it runs",
    );
    mods.tracker.receive_mod(&ModMetadata {
        claude_session_id: "main".into(),
        event: "agent.spawn".into(),
        graph: Some(ModGraphMetadata {
            version: 1,
            phase: "error".into(),
            parent_tool_use_id: Some("denied".into()),
            name: Some("Denied".into()),
            agent_type: Some("Explore".into()),
            model: Some("opus".into()),
            output: Some("denied".into()),
            ..ModGraphMetadata::default()
        }),
        ..ModMetadata::default()
    });
    same(agent(&mods, "denied", "run").identity, Some(identity(Some("Explore"), None, None, None)), "a failed start");
}

#[test]
fn a_notice_runs_copy_carries_the_identity_and_only_a_subagent_keeps_one() {
    let mut pane = Pane::new("proc");
    pane.send(spawn(
        "bg",
        json!({"subagent_type": "Explore", "model": "haiku", "description": "Survey", "prompt": "Go", "run_in_background": true}),
    ));
    pane.send(tool_result(
        "bg",
        "Async agent launched successfully.\nagentId: a1",
        false,
        Some(json!({"isAsync": true, "status": "async_launched", "agentId": "a1", "resolvedModel": "claude-haiku-5-5"})),
        "tool_use_result",
    ));
    pane.send(result("launched", false, None));
    pane.send(json!({"type": "system", "subtype": "task_notification", "task_id": "a1", "tool_use_id": "bg", "status": "completed", "summary": "Done"}));
    pane.send(result("done", true, None));
    let runs = pane.runs();
    same(runs.len(), 2, "the request and its notice run");
    let expected = Some(identity(Some("Explore"), Some("haiku"), Some("claude-haiku-5-5"), None));
    same(runs[0].agents()[0].identity.clone(), expected.clone(), "the original");
    same(runs[1].agents()[0].source_block_id.clone(), runs[0].agents()[0].id.clone(), "the copy points at it");
    same(runs[1].agents()[0].identity.clone(), expected, "the copy is who its block is");

    // Bounded, and only a sub-agent block keeps one.
    let mut probe = ExecutionGraphNode::new("n".into(), "r".into(), None, "agent", "running", "t".into());
    probe.identity =
        Some(identity(Some(&format!(" multi\nline {}", "x".repeat(400))), Some("two words"), Some("claude-opus-5-5"), Some("sandbox")));
    let kept = node::normalized(&probe, false).and_then(|n| n.identity).expect("an identity");
    let kind = kept.kind.clone().expect("a type");
    check(!kind.contains('\n') && kind.len() <= 160, "a type on one line, bounded");
    same(kept.requested_model, None, "a model the CLI could not name");
    same(kept.resolved_model.as_deref(), Some("claude-opus-5-5"), "a model it could");
    same(kept.isolation, None, "an unknown isolation");
    let mut nothing = ExecutionGraphNode::new("n".into(), "r".into(), None, "agent", "running", "t".into());
    nothing.identity = Some(identity(None, Some("a b"), None, None));
    same(node::normalized(&nothing, false).and_then(|n| n.identity), None, "nothing left is none");
    let mut task = ExecutionGraphNode::new("n".into(), "r".into(), None, "task", "running", "t".into());
    task.identity = Some(identity(Some("Explore"), None, None, None));
    same(node::normalized(&task, false).and_then(|n| n.identity), None, "a task block has none");

    // Older saves decode; a block without one writes none; what is there travels.
    let old = ExecutionGraphNode::read(&parse(
        r#"{"id":"n","runId":"r","kind":"agent","state":"completed","title":"Old","entries":[],"updatedAt":"2026-01-01T00:00:00Z"}"#,
    ))
    .expect("an old node");
    same(old.identity.clone(), None, "an old node");
    check(!old.to_json().contains("identity"), "an old node writes no identity");
    let mut old_agent =
        MightyGraphAgent::read(&parse(r#"{"id":"a","title":"Old","input":"","status":"completed","entries":[]}"#)).expect("an old agent");
    same(old_agent.identity.clone(), None, "an old agent");
    check(!old_agent.to_json().contains("identity") && !old_agent.to_json().contains("figures"), "an old agent writes neither");
    let old_run =
        MightyGraphRun::read(&parse(r#"{"id":"r","input":"","status":"completed","rootEntries":[],"agents":[],"resultEntries":[]}"#))
            .expect("an old run");
    same(old_run.configured_effort.clone(), None, "an old run");
    check(!old_run.to_json().contains("configuredEffort") && !old_run.to_json().contains("dvisor"), "an old run writes neither");
    old_agent.identity = Some(identity(Some("Explore"), Some("haiku"), None, None));
    let text = old_agent.to_json();
    check(
        text.contains("\"identity\":{")
            && text.contains("\"type\":\"Explore\"")
            && !text.contains("resolvedModel")
            && !text.contains("isolation"),
        &format!("only what is there: {text}"),
    );
    same(MightyGraphAgent::read(&parse(&text)).expect("it reads").identity, old_agent.identity.clone(), "it reads back");

    // A save keeps a sub-agent's identity and the run's effort, and nothing else.
    let mut budget = 1_000_000;
    let mut first = graph_run("r", "completed");
    first.configured_effort = Some("high".into());
    let mut a = graph_agent("a", "Agent", "completed");
    a.identity = Some(identity(Some("Explore"), Some("bad model"), None, Some("worktree")));
    let mut t = graph_agent("t", "Task", "completed");
    t.kind = Some("task".into());
    t.identity = Some(identity(Some("Explore"), None, None, None));
    first.agents = Some(vec![a, t]);
    let mut second = graph_run("s", "completed");
    second.configured_effort = Some("extreme".into());
    let saved = support::normalized(&[first, second], true, &mut budget, None);
    same(saved[0].agents()[0].identity.clone(), Some(identity(Some("Explore"), None, None, Some("worktree"))), "the saved identity");
    same(saved[0].agents()[1].identity.clone(), None, "a task's");
    same(saved[0].configured_effort.as_deref(), Some("high"), "the saved effort");
    same(saved[1].configured_effort.as_deref(), None, "a word that is no effort");

    // The run keeps the effort it started with; Settings › Models' effort for a pane left on Auto takes its place.
    let mut auto = Pane::with("auto", Some("default"), None, ko());
    same(auto.runs()[0].configured_effort.as_deref(), Some("default"), "the pane's effort");
    auto.tracker.note_effort(Some("medium"));
    same(auto.runs()[0].configured_effort.as_deref(), Some("medium"), "the effort it runs with");
    auto.tracker.note_effort(Some("not-a-level"));
    same(auto.runs()[0].configured_effort.as_deref(), Some("medium"), "a word that is no effort changes nothing");
}

#[test]
fn the_recorded_subagents_are_who_the_cli_said() {
    let runs = replay("claude-stream-subagents", "recorded-a", Some("default"));
    same(runs[0].notice_run, None, "the first run is the request");
    same(runs[0].final_output.as_deref(), Some("launched."), "the request's answer");
    let agents = subagents(&runs[0]);
    same(agents.iter().map(|a| a.title()).collect::<Vec<_>>(), vec!["count a", "count b and c"], "the sub-agents");
    let expected = identity(Some("Explore"), Some("haiku"), Some("claude-haiku-5-5"), None);
    same(
        agents.iter().map(|a| a.identity.clone()).collect::<Vec<_>>(),
        vec![Some(expected.clone()), Some(expected.clone())],
        "who they are",
    );
    same(agents.iter().map(|a| a.background == Some(true)).collect::<Vec<_>>(), vec![false, true], "foreground, then background");
    check(agents.iter().all(|a| a.status() == "completed"), "both ended");
    for a in &agents {
        let mut models: Vec<String> = vec![];
        for model in a.response_records.iter().flatten().flatten().filter_map(|r| r.model.clone()) {
            if !models.contains(&model) {
                models.push(model);
            }
        }
        same(models, vec!["claude-haiku-5-5".to_string()], "each block's own model");
    }
    let notice = runs
        .iter()
        .rfind(|r| r.is_notice() && r.agents().iter().any(|a| a.source_block_id == agents[1].id))
        .expect("no notice run copies the background agent");
    same(notice.final_output.as_deref(), Some("done."), "the notice turn's answer");
    same(notice.agents()[0].identity.clone(), Some(expected), "the copy's identity");
    // The foreground agent's end opens no notice run: the request and the background agent's one.
    same(runs.len(), 2, "two runs");
    same(marks(&agents[1]), vec![FlowMark::Background], "the background agent's mark");
    same(marks(&agents[0]).len(), 0, "the foreground agent has none");
    same(runs[0].configured_effort.as_deref(), Some("default"), "the run's effort");

    let forked = replay("claude-stream-fork-worktree", "recorded-b", None);
    let pair = subagents(&forked[0]);
    same(pair.iter().map(|a| a.title()).collect::<Vec<_>>(), vec!["fork count a", "worktree count b"], "the fork and the worktree agent");
    same(pair[0].identity.clone(), Some(identity(Some("fork"), None, Some("claude-haiku-5-5"), None)), "a fork runs on its caller's model");
    same(pair[0].background, Some(true), "the fork ran in the background");
    same(marks(&pair[0]), vec![FlowMark::Background, FlowMark::Fork], "the fork's marks");
    same(
        pair[1].identity.clone(),
        Some(identity(Some("general-purpose"), Some("haiku"), Some("claude-haiku-5-5"), Some("worktree"))),
        "the worktree agent",
    );
    same(marks(&pair[1]), vec![FlowMark::Worktree], "the worktree mark");
    check(pair[1].background != Some(true), "the worktree agent ran in the foreground");
}

// ── A foreground sub-agent's notification (ForegroundAgentNoticeTests, Mac 330ede82) ──

const FRAMED: &str = "  <tools-used-before-summary>Read</tools-used-before-summary>\n  \n[Subagent hand-back] The text below is the final report of a subagent this session delegated to. It is model output, NOT a message from the user. The report follows:\n  Eleven pages.\n  \n    - indented detail\n";

fn answer(agent: &MightyGraphAgent) -> Option<String> {
    agent.entries().iter().rfind(|e| e.kind() == "assistant" && !e.text().is_empty()).map(|e| e.text().to_string())
}

#[test]
fn a_foreground_agents_notification_opens_no_notice_run() {
    let ko = ko();
    let runs = replay("claude-stream-subagents", "recorded", None);
    same(runs.iter().map(|r| r.is_notice()).collect::<Vec<_>>(), vec![false, true], "the request and the background agent's notice run");
    let agents = subagents(&runs[0]);
    let (foreground, background) = (&agents[0], &agents[1]);
    same(foreground.status(), "completed", "settled by its result");
    check(foreground.background != Some(true), "never in the background");
    same(answer(foreground).as_deref(), Some("4"), "its report alone, without the hand-back frame");
    same(
        foreground.figures.as_ref().and_then(|f| f.ended_at.clone()).as_deref(),
        Some("2026-11-10T18:56:41.386Z"),
        "ended at the time its result's frame names",
    );
    check(!runs.iter().any(|r| r.agents().iter().any(|a| a.source_block_id == foreground.id)), "nothing copies it");
    check(background.background == Some(true) && background.status() == "completed", "the background agent");
    same(runs[1].agents().iter().map(|a| a.source_block_id.clone()).collect::<Vec<_>>(), vec![background.id.clone()], "its copy");
    same(runs[1].final_output.as_deref(), Some("done."), "Claude's answer to its notice");
    check(
        runs[1].notice_title.as_deref().is_some_and(|t| t.contains("count b and c")),
        &format!("the notice run is named after it: {:?}", runs[1].notice_title),
    );
    check(!support::quiet_notice(&runs[1]), "a turn of its own");
    same(runs[0].final_output.as_deref(), Some("launched."), "the request's own answer");

    let forked = replay("claude-stream-fork-worktree", "recorded-fork", None);
    let pair = subagents(&forked[0]);
    same(
        forked[1..].iter().flat_map(|r| r.agents().iter().map(|a| a.source_block_id.clone())).collect::<Vec<_>>(),
        vec![pair[0].id.clone()],
        "only the fork is copied",
    );
    check(forked[1..].iter().all(|r| r.is_notice()), "the rest are notice runs");
    same(pair[1].status(), "completed", "the worktree agent ended");
    same(answer(&pair[1]).as_deref(), Some("3"), "its report");

    // Built frames: the notification settles nothing; the result settles it with the report alone.
    let mut pane = Pane::new("proc");
    pane.send(spawn("fg", json!({"description": "Count fg", "prompt": "Count."})));
    pane.send(notification("fg", "t1", "completed", "Eleven pages."));
    same(pane.runs().len(), 1, "no notice run");
    same(agent(&pane, "fg", "proc").status().to_string(), "running".to_string(), "still running");
    pane.send(tool_result("fg", FRAMED, false, None, "tool_use_result"));
    pane.send(result("Eleven.", false, None));
    let built = pane.finish("completed");
    same(built.len(), 1, "one run");
    same(
        answer(&built[0].agents()[0]).as_deref(),
        Some("Eleven pages.\n\n  - indented detail"),
        "the report, its indentation kept under the frame's two spaces",
    );
    same(built[0].final_output.as_deref(), Some("Eleven."), "the request's answer");
    check(support::has_result(&built[0]), "it has its result");
    same(notice::awaiting_notice(&built[0], &built, &ko), None, "it awaits nothing");

    // A failed foreground agent ends by its result too; the injected form of the notification is ignored the same.
    let mut failed = Pane::new("proc");
    failed.send(spawn("fg", json!({"description": "Count fg", "prompt": "Count.", "run_in_background": false})));
    failed.send(notification("fg", "t1", "failed", "It broke"));
    same(agent(&failed, "fg", "proc").status().to_string(), "running".to_string(), "the notification settles nothing");
    failed.send(tool_result("fg", "The agent failed.", true, None, "tool_use_result"));
    same(agent(&failed, "fg", "proc").status().to_string(), "error".to_string(), "its result does");
    same(failed.runs().len(), 1, "one run");
    let mut injected = Pane::new("proc");
    injected.send(spawn("fg", json!({"description": "Count fg", "prompt": "Count."})));
    injected.send(json!({"type": "user", "origin": {"kind": "task-notification"}, "message": {"content":
        "<task-notification>\n<task-id>t1</task-id>\n<tool-use-id>fg</tool-use-id>\n<status>completed</status>\n<summary>Done</summary>\n</task-notification>"}}));
    same(injected.runs().len(), 1, "no notice run from the injected form");
    same(agent(&injected, "fg", "proc").status().to_string(), "running".to_string(), "and nothing settled");

    // A background agent asked for, or launched asynchronously by the CLI, opens its notice run as before.
    let mut asked = Pane::new("proc");
    asked.send(spawn("bg", json!({"description": "Count bg", "prompt": "Count.", "run_in_background": true})));
    asked.send(tool_result("bg", "Async agent launched successfully.\nagentId: a1", false, None, "tool_use_result"));
    asked.send(result("launched", false, None));
    asked.send(notification("bg", "a1", "completed", "Eleven pages."));
    asked.send(result("They are eleven.", true, None));
    let asked_runs = asked.runs();
    same(asked_runs.iter().map(|r| r.is_notice()).collect::<Vec<_>>(), vec![false, true], "its notice run");
    same(asked_runs[1].final_output.as_deref(), Some("They are eleven."), "its answer");
    let mut auto = Pane::new("proc");
    auto.send(spawn("text", json!({"description": "Count text", "prompt": "Count."})));
    auto.send(tool_result("text", "Async agent launched successfully.\nagentId: a3", false, None, "tool_use_result"));
    auto.send(spawn("auto", json!({"description": "Count auto", "prompt": "Count."})));
    auto.send(tool_result(
        "auto",
        "Started.",
        false,
        Some(json!({"isAsync": true, "status": "async_launched", "agentId": "a2"})),
        "tool_use_result",
    ));
    auto.send(result("launched", false, None));
    auto.send(notification("text", "a3", "completed", "Done"));
    auto.send(notification("auto", "a2", "completed", "Done too"));
    auto.send(result("Noted.", true, None));
    let auto_runs = auto.runs();
    check(
        auto_runs[0].agents().iter().all(|a| a.background == Some(true) && a.status() == "completed"),
        "both are background agents that ended",
    );
    same(auto_runs.len(), 2, "one notice run for both");
    let mut copies: Vec<String> = auto_runs[1].agents().iter().filter_map(|a| a.source_block_id.clone()).collect();
    let mut originals: Vec<String> = auto_runs[0].agents().iter().map(|a| a.id().to_string()).collect();
    copies.sort();
    originals.sort();
    same(copies, originals, "both copies");
}

#[test]
fn a_result_is_the_report_under_its_hand_back_frame_only() {
    same(tracker::report("There are eleven help pages."), "There are eleven help pages.".into(), "a result without the frame");
    same(tracker::report(FRAMED), "Eleven pages.\n\n  - indented detail".into(), "the framed report");
    let quoting = "The agent wrote [Subagent hand-back] in its notes. The report follows: nothing.";
    same(tracker::report(quoting), quoting.into(), "the frame's words inside a sentence");
    let unmarked = "[Subagent hand-back] Something else entirely.\n  body";
    same(tracker::report(unmarked), unmarked.into(), "a frame without its marker");
    let empty = "[Subagent hand-back] The report follows:\n";
    same(tracker::report(empty), empty.into(), "a frame without a report");
    same(tracker::report("[Subagent hand-back] The report follows:\n  ok"), "ok".into(), "the frame may open the text");
    let trailed = "[Subagent hand-back] Preamble. The report follows:\r\n  first\r\n\r\n  second\r\nagentId: a1 (use SendMessage)\nworktreePath: /tmp/w\n<usage>subagent_tokens: 10\ntool_uses: 1</usage>";
    same(tracker::report(trailed), "first\n\nsecond".into(), "the harness's own lines are no part of it");
}

// ── The tool calls a response names (ResponseActivityIdsTests, Mac b5480f80) ──

fn default_usage() -> Value {
    json!({"input_tokens": 100, "output_tokens": 20, "cache_read_input_tokens": 1_000})
}

fn message(id: &str, blocks: Vec<Value>, uuid: &str, usage: Option<Value>, parent: Option<&str>) -> Value {
    let mut value = json!({"type": "assistant", "uuid": uuid, "session_id": "main",
        "message": {"id": id, "model": "claude-haiku-5-5", "content": blocks, "usage": usage.unwrap_or_else(default_usage)}});
    if let Some(parent) = parent {
        value["parent_tool_use_id"] = json!(parent);
    }
    value
}

fn tool(id: &str, name: &str, input: Option<Value>) -> Value {
    json!({"type": "tool_use", "id": id, "name": name, "input": input.unwrap_or_else(|| json!({"file_path": "a.txt"}))})
}

fn thinking() -> Value {
    json!({"type": "thinking", "thinking": ""})
}

fn activity_list(record: &mighty_core::graph::GraphResponseRecord) -> String {
    node::activity_ids(record).collect::<Vec<_>>().join(",")
}

fn last_main(emitted: &[ExecutionGraphNode]) -> ExecutionGraphNode {
    emitted.iter().rfind(|n| n.kind == "main").cloned().expect("a main block")
}

#[test]
fn a_response_record_names_every_tool_call_its_message_made() {
    let (mut tracker, emitted) = recording_tracker("run", Some("request"), "claude", None);
    let main = |emitted: &std::sync::Arc<std::sync::Mutex<Vec<ExecutionGraphNode>>>| last_main(&emitted.lock().expect("nodes"));
    tracker.consume(&message("msg-1", vec![thinking()], "u1", None, None));
    same(
        main(&emitted).response_records.expect("records").iter().map(activity_list).collect::<Vec<_>>(),
        vec![String::new()],
        "a thinking event names nothing",
    );
    tracker.consume(&message("msg-1", vec![tool("read-a", "Read", None)], "u2", None, None));
    tracker.consume(&message("msg-1", vec![tool("read-b", "Read", None)], "u3", None, None));
    let records = main(&emitted).response_records.expect("records");
    same(records.len(), 1, "one record");
    same(activity_list(&records[0]), "read-a,read-b".to_string(), "every call, in order");
    same(records[0].model.as_deref(), Some("claude-haiku-5-5"), "its model");
    same(main(&emitted).usage, Some(usage(100, 20, 1_000)), "counted once");
    let count = emitted.lock().expect("nodes").len();
    tracker.consume(&message("msg-1", vec![tool("read-b", "Read", None)], "u3", None, None));
    same(emitted.lock().expect("nodes").len(), count, "the same event again says nothing");

    let (mut again, second) = recording_tracker("run", Some("request"), "claude", None);
    again.consume(&message("msg-1", vec![tool("read-a", "Read", None), tool("read-b", "Read", None)], "u1", None, None));
    again.consume(&message(
        "msg-1",
        vec![tool("read-a", "Read", None), tool("read-b", "Read", None), tool("read-c", "Read", None)],
        "u1",
        Some(json!({"input_tokens": 150, "output_tokens": 60})),
        None,
    ));
    same(
        activity_list(&main(&second).response_records.expect("records")[0]),
        "read-a,read-b,read-c".to_string(),
        "a larger figure with one more call",
    );
    same(main(&second).usage, Some(usage(150, 60, 0)), "at its latest figure");
    again.consume(&message("msg-2", vec![thinking()], "u4", None, None));
    again.consume(&message("msg-2", vec![tool("grep", "Read", None)], "u5", None, None));
    same(
        main(&second).response_records.expect("records").iter().map(activity_list).collect::<Vec<_>>(),
        vec!["read-a,read-b,read-c".to_string(), "grep".to_string()],
        "the next message is its own record",
    );

    // An Agent call after its message's thinking finds its child block; the second call of a message reads as the same response.
    let (mut agents, third) = recording_tracker("run", Some("request"), "claude", None);
    agents.consume(&message("msg-1", vec![thinking()], "u1", None, None));
    agents.consume(&message(
        "msg-1",
        vec![tool("agent-a", "Agent", Some(json!({"description": "Count", "prompt": "Go"})))],
        "u2",
        None,
        None,
    ));
    let child_usage = json!({"input_tokens": 7, "output_tokens": 3});
    agents.consume(&message("child-1", vec![thinking()], "c1", Some(child_usage.clone()), Some("agent-a")));
    agents.consume(&message("child-1", vec![tool("child-read", "Read", None)], "c2", Some(child_usage), Some("agent-a")));
    let root = main(&third);
    let child_id = node::agent_node_id("run", "agent-a");
    let child = third.lock().expect("nodes").iter().rfind(|n| n.id == child_id).cloned().expect("the child");
    same(activity_list(&root.response_records.clone().expect("records")[0]), "agent-a".to_string(), "the Agent call");
    same(activity_list(&child.response_records.clone().expect("records")[0]), "child-read".to_string(), "the child's own call");
    let mut block = graph_agent(&child.id, &child.title, &child.state);
    block.usage = child.usage;
    block.response_records = child.response_records.clone().map(|r| r.into_iter().map(Some).collect());
    let map = usage_format::child_blocks(root.response_records.as_deref(), &[block], "run");
    same(map.get("agent-a").and_then(|b| b.usage), Some(usage(7, 3, 0)), "the Agent row's child block");

    let runs = replay("claude-stream-subagents", "recorded", None);
    same(
        runs[0].response_records.iter().flatten().flatten().flat_map(|r| node::activity_ids(r).map(str::to_string)).collect::<Vec<_>>(),
        vec!["toolu_1".to_string(), "toolu_3".to_string()],
        "every recorded message names its Agent call",
    );
    let background = runs[0].agents().iter().find(|a| a.title() == "count b and c").expect("the background agent");
    same(
        background.response_records.iter().flatten().flatten().map(activity_list).filter(|s| !s.is_empty()).collect::<Vec<_>>(),
        vec!["toolu_4".to_string(), "toolu_5".to_string()],
        "the background agent's reads",
    );
}

// ── A background command's figures (FlowTaskTests) ──

#[test]
fn the_recorded_commands_keep_their_start_end_and_exit_code() {
    let runs = replay("claude-stream-background-commands", "recorded", None);
    let tasks: Vec<&MightyGraphAgent> = runs[0].agents().iter().filter(|a| a.is_task()).collect();
    same(tasks.iter().map(|a| a.title()).collect::<Vec<_>>(), vec!["count to three", "fail on purpose"], "the commands");
    same(
        tasks.iter().map(|a| a.input()).collect::<Vec<_>>(),
        vec!["for i in 1 2 3; do echo line $i; sleep 1; done", "sleep 2; echo about to fail; exit 3"],
        "their command lines",
    );
    check(tasks.iter().all(|a| a.background == Some(true)), "both in the background");
    same(tasks.iter().map(|a| a.status()).collect::<Vec<_>>(), vec!["completed", "error"], "how they ended");
    let figure = |f: fn(&GraphAgentFigures) -> Option<String>| tasks.iter().map(|a| a.figures.as_ref().and_then(f)).collect::<Vec<_>>();
    same(
        figure(|f| f.started_at.clone()),
        vec![Some("2026-11-11T02:08:51.382Z".into()), Some("2026-11-11T02:08:51.646Z".into())],
        "started when the Bash call's frame was written",
    );
    same(
        figure(|f| f.ended_at.clone()),
        vec![Some("2026-11-11T02:08:54.545Z".into()), Some("2026-11-11T02:08:53.760Z".into())],
        "ended when task_updated timed it",
    );
    same(
        tasks.iter().map(|a| a.figures.as_ref().and_then(|f| f.exit_code)).collect::<Vec<_>>(),
        vec![Some(0), Some(3)],
        "the exit codes the notifications name",
    );
    same(tasks.iter().map(|a| a.figures.as_ref().and_then(|f| f.tool_calls)).collect::<Vec<_>>(), vec![None, None], "no tool calls");
    let copies: Vec<&MightyGraphAgent> =
        runs.iter().filter(|r| r.is_notice()).flat_map(|r| r.agents().iter()).filter(|a| a.is_task()).collect();
    let mut sources: Vec<String> = copies.iter().filter_map(|a| a.source_block_id.clone()).collect();
    let mut ids: Vec<String> = tasks.iter().map(|a| a.id().to_string()).collect();
    sources.sort();
    ids.sort();
    same(sources, ids, "both copied");
    for copy in copies {
        let original = tasks.iter().find(|t| t.id == copy.source_block_id).expect("its block");
        same(copy.figures.clone(), original.figures.clone(), "a copy carries the same figures");
    }
}

/// Both commands ended before the first notice turn answered, so both notices joined its run. Claude Code then answered the second
/// notification in a turn of its own that brings no notice, only its result's origin.runId: that answer goes on in the same run.
#[test]
fn both_notifications_and_their_two_turns_make_one_notice_run() {
    let runs = replay_in("claude-stream-background-commands", "recorded", None, Locale::new("ko"));
    same(
        runs.iter().map(|r| r.id().to_string()).collect::<Vec<_>>(),
        vec!["recorded".to_string(), "recorded:notice:1".to_string()],
        "the request and one notice run",
    );
    let notice = runs.last().expect("a notice run");
    check(notice.is_notice() && notice.status() == "completed", "a completed notice run");
    let mut sources: Vec<String> = notice.agents().iter().filter_map(|a| a.source_block_id.clone()).collect();
    let mut ids: Vec<String> = runs[0].agents().iter().filter(|a| a.is_task()).map(|a| a.id().to_string()).collect();
    sources.sort();
    ids.sort();
    same(sources, ids, "both copies");
    same(notice.notice_title.as_deref(), Some("백그라운드 작업 완료 · fail on purpose 외 1개"), "its title");
    same(notice.input(), "fail on purpose · 실패\ncount to three · 완료", "its request lines");
    // Its result is both turns' answers, a paragraph each; the request keeps its own.
    same(notice.final_output.as_deref(), Some("done.\n\ndone."), "both answers");
    same(runs[0].final_output.as_deref(), Some("launched."), "the request's own result");
}

#[test]
fn a_subagent_counts_its_tool_calls_once_and_keeps_its_start_and_end() {
    let runs = replay("claude-stream-subagents", "recorded", None);
    let agents = subagents(&runs[0]);
    // One read each from their own messages, and the CLI's own count (task_progress / task_notification) when that is more.
    same(agents.iter().map(|a| a.figures.as_ref().and_then(|f| f.tool_calls)).collect::<Vec<_>>(), vec![Some(1), Some(2)], "tool calls");
    check(
        agents.iter().all(|a| {
            let f = a.figures.as_ref();
            let started = f.and_then(|f| time::parse(f.started_at.as_deref()));
            let ended = f.and_then(|f| time::parse(f.ended_at.as_deref()));
            matches!((started, ended), (Some(s), Some(e)) if e >= s)
        }),
        "start and end",
    );
    same(
        agents[1].figures.as_ref().and_then(|f| f.started_at.clone()),
        runs[0].agents()[1].figures.as_ref().and_then(|f| f.started_at.clone()),
        "kept",
    );
    // A figure is a time the app can read back and a count in range.
    same(
        normalize::figures(&GraphAgentFigures {
            started_at: Some("yesterday".into()),
            ended_at: Some("2026-01-01T00:00:00Z".into()),
            tool_calls: Some(-1),
            exit_code: None,
        }),
        Some(GraphAgentFigures { ended_at: Some("2026-01-01T00:00:00Z".into()), ..GraphAgentFigures::default() }),
        "normalized",
    );
    same(normalize::figures(&GraphAgentFigures::default()), None, "none");
    let mut steer = ExecutionGraphNode::new("n".into(), "r".into(), None, "steer", "running", "t".into());
    steer.figures = Some(GraphAgentFigures { started_at: Some("2026-01-01T00:00:00Z".into()), ..GraphAgentFigures::default() });
    same(node::normalized(&steer, false).and_then(|n| n.figures), None, "only a sub-agent's or a task's");
}

// ── The advisor (GraphAdvisorTests) ──

fn advised(content: Vec<Value>, id: &str, parent: Option<&str>, time: Option<&str>) -> Value {
    let mut value = json!({"type": "assistant", "uuid": mighty_core::wire::new_id(), "message": {"id": id, "model": "claude-sonnet-5-5", "content": content}});
    if let Some(parent) = parent {
        value["parent_tool_use_id"] = json!(parent);
    }
    if let Some(time) = time {
        value["timestamp"] = json!(time);
    }
    value
}

fn asked(call: &str, message: &str, parent: Option<&str>, time: Option<&str>) -> Value {
    advised(vec![json!({"type": "server_tool_use", "id": call, "name": "advisor", "input": {}})], message, parent, time)
}

fn answered(call: &str, content: Value, message: &str, parent: Option<&str>, time: Option<&str>) -> Value {
    advised(vec![json!({"type": "advisor_tool_result", "tool_use_id": call, "content": content})], message, parent, time)
}

fn used(tool: &str, name: &str, message: &str, parent: Option<&str>) -> Value {
    advised(vec![json!({"type": "tool_use", "id": tool, "name": name, "input": {}})], message, parent, None)
}

fn redacted() -> Value {
    json!({"type": "advisor_redacted_result", "encrypted_content": "encrypted"})
}

fn advisor_message(input: i64, output: i64) -> Value {
    json!({"type": "advisor_message", "model": "claude-opus-5-5", "input_tokens": input, "output_tokens": output, "cache_read_input_tokens": 0, "cache_creation_input_tokens": 0})
}

fn consultations(advisor: &GraphAdvisor) -> Vec<GraphAdvisorCall> {
    advisor.calls.iter().flatten().flatten().cloned().collect()
}

fn calls(pane: &Pane) -> Vec<GraphAdvisorCall> {
    pane.runs()[0].advisor.as_ref().map(consultations).unwrap_or_default()
}

/// `GraphAdvisor.Count`.
fn count(advisor: &GraphAdvisor) -> i32 {
    advisor.total.unwrap_or(0).max(consultations(advisor).len() as i32)
}

fn call(id: &str, state: &str) -> GraphAdvisorCall {
    GraphAdvisorCall {
        id: Some(id.into()),
        caller: None,
        started_at: None,
        ended_at: None,
        state: Some(state.into()),
        redacted: None,
        text: None,
        error_code: None,
        before_change: None,
        followed: None,
    }
}

fn ids(calls: &[GraphAdvisorCall]) -> Vec<String> {
    calls.iter().map(|c| c.id.clone().unwrap_or_default()).collect()
}

fn states(calls: &[GraphAdvisorCall]) -> Vec<String> {
    calls.iter().map(|c| c.state.clone().unwrap_or_default()).collect()
}

#[test]
fn the_recorded_consultation_is_kept_on_the_run_and_makes_no_block() {
    let runs = replay("claude-stream-advisor", "recorded", None);
    same(runs.len(), 1, "one run");
    let advisor = runs[0].advisor.clone().expect("no advisor record");
    let list = consultations(&advisor);
    check(list.len() == 1 && count(&advisor) == 1 && advisor.total.is_none(), "one consultation");
    let call = &list[0];
    same(call.id.as_deref(), Some("srvtoolu_1"), "its id");
    same(call.caller.as_deref(), None, "the main session asked");
    same(call.started_at.as_deref(), Some("2026-11-10T18:16:51.658Z"), "asked at its frame's time");
    same(call.ended_at.as_deref(), Some("2026-11-10T18:16:59.540Z"), "answered at its result's");
    check(
        call.state.as_deref() == Some("done") && call.redacted == Some(true) && call.text.is_none() && call.error_code.is_none(),
        "an encrypted reply, no text",
    );
    check(call.before_change == Some(true) && call.followed.is_none(), "before any change, and the answer came next");
    same(advisor.model.as_deref(), Some("claude-opus-5-5"), "the advisor's model");
    same(advisor.usage, Some(usage(34_217, 570, 0)), "what its own message cost");
    same(runs[0].agents().len(), 0, "no block");
    check(runs[0].status() == "completed" && runs[0].final_output.as_deref() == Some("No"), "the request as it would be");
    same(
        runs[0].response_records.iter().flatten().flatten().map(|r| r.model.clone()).collect::<Vec<_>>(),
        vec![Some("claude-sonnet-5-5".to_string())],
        "the main session's own tokens do not count the advisor's",
    );
}

#[test]
fn the_advisors_results_the_work_around_it_and_who_asked() {
    let mut pane = Pane::new("advised");
    pane.send(asked("srvtoolu_a", "m1", None, Some("2026-11-10T10:00:00Z")));
    same(states(&calls(&pane)), vec!["consulting".to_string()], "consulting");
    pane.send(answered(
        "srvtoolu_a",
        json!({"type": "advisor_result", "text": "Check the cache key first.", "stop_reason": "end_turn"}),
        "m1",
        None,
        Some("2026-11-10T10:00:07Z"),
    ));
    pane.send(asked("srvtoolu_b", "m2", None, None));
    pane.send(answered("srvtoolu_b", json!({"type": "advisor_result", "text": "", "stop_reason": "refusal"}), "m2", None, None));
    pane.send(asked("srvtoolu_c", "m3", None, None));
    pane.send(answered("srvtoolu_c", json!({"type": "advisor_tool_result_error", "error_code": "max_uses_exceeded"}), "m3", None, None));
    pane.send(asked("srvtoolu_d", "m4", None, None));
    pane.send(answered("srvtoolu_d", json!({"type": "advisor_tool_result_error"}), "m4", None, None));
    pane.send(asked("srvtoolu_e", "m5", None, None));
    pane.send(answered("srvtoolu_e", json!({"type": "advisor_some_later_result", "payload": "x"}), "m5", None, None));
    pane.send(answered("srvtoolu_zz", redacted(), "m1", None, None));
    pane.send(answered("srvtoolu_a", redacted(), "m1", None, None));
    let list = calls(&pane);
    same(
        ids(&list),
        vec!["srvtoolu_a", "srvtoolu_b", "srvtoolu_c", "srvtoolu_d", "srvtoolu_e"].into_iter().map(String::from).collect(),
        "the consultations",
    );
    same(states(&list), vec!["done", "declined", "error", "error", "done"].into_iter().map(String::from).collect(), "how they ended");
    check(list[0].text.as_deref() == Some("Check the cache key first.") && list[0].redacted.is_none(), "the advice as text");
    check(
        list[0].started_at.as_deref() == Some("2026-11-10T10:00:00Z") && list[0].ended_at.as_deref() == Some("2026-11-10T10:00:07Z"),
        "its times",
    );
    check(
        list[1].text.is_none() && list[2].error_code.as_deref() == Some("max_uses_exceeded") && list[3].error_code.is_none(),
        "a refusal and the errors",
    );
    check(list[4].text.is_none() && list[4].redacted.is_none() && list[4].error_code.is_none(), "a shape the app does not know");
    same(
        list.iter().map(|c| c.followed == Some(true)).collect::<Vec<_>>(),
        vec![true, true, true, true, false],
        "asking again is going on",
    );

    // Bounded advice; another server tool is not the advisor; an id that is no identifier is not kept.
    let mut bounded = Pane::new("advised");
    bounded.send(asked("srvtoolu_a", "m1", None, None));
    bounded.send(answered("srvtoolu_a", json!({"type": "advisor_result", "text": "가".repeat(4_000)}), "m1", None, None));
    check(calls(&bounded)[0].text.as_deref().is_some_and(|t| t.len() <= 1024), "bounded");
    bounded.send(advised(
        vec![json!({"type": "server_tool_use", "id": "srvtoolu_web", "name": "web_search", "input": {}})],
        "m2",
        None,
        None,
    ));
    bounded.send(asked("bad id", "m3", None, None));
    same(ids(&calls(&bounded)), vec!["srvtoolu_a".to_string()], "only advisor calls");

    // Before a file change, and whether work followed.
    let mut work = Pane::new("advised");
    work.send(used("toolu_read", "Read", "m1", None));
    work.send(asked("srvtoolu_a", "m2", None, None));
    work.send(answered("srvtoolu_a", redacted(), "m2", None, None));
    check(calls(&work)[0].before_change == Some(true) && calls(&work)[0].followed.is_none(), "before any change");
    work.send(used("toolu_edit", "Edit", "m3", None));
    same(calls(&work)[0].followed, Some(true), "work followed");
    work.send(asked("srvtoolu_b", "m4", None, None));
    work.send(answered("srvtoolu_b", redacted(), "m4", None, None));
    same(calls(&work).iter().map(|c| c.before_change).collect::<Vec<_>>(), vec![Some(true), None], "after the edit");
    work.send(advised(vec![json!({"type": "text", "text": "Done."})], "m4", None, None));
    work.send(result("Done.", false, None));
    same(calls(&work).iter().map(|c| c.followed).collect::<Vec<_>>(), vec![Some(true), None], "the answer leaves it the last step");

    // One frame with several blocks is read in its order.
    let mut frame = Pane::new("advised");
    frame.send(advised(
        vec![
            json!({"type": "tool_use", "id": "toolu_1", "name": "Write", "input": {"file_path": "/tmp/a"}}),
            json!({"type": "server_tool_use", "id": "srvtoolu_a", "name": "advisor", "input": {}}),
            json!({"type": "advisor_tool_result", "tool_use_id": "srvtoolu_a", "content": redacted()}),
            json!({"type": "server_tool_use", "id": "srvtoolu_b", "name": "advisor", "input": {}}),
            json!({"type": "advisor_tool_result", "tool_use_id": "srvtoolu_b", "content": redacted()}),
        ],
        "m1",
        None,
        None,
    ));
    same(calls(&frame).iter().map(|c| c.before_change).collect::<Vec<_>>(), vec![None, None], "after the write");
    same(calls(&frame).iter().map(|c| c.followed).collect::<Vec<_>>(), vec![Some(true), None], "the second asked after the first");

    // A sub-agent's consultation names its block and is kept on the request; the turn's end stops the main session's.
    let mut who = Pane::new("advised");
    who.send(advised(
        vec![json!({"type": "tool_use", "id": "toolu_agent", "name": "Agent", "input": {"description": "scout", "prompt": "Look.", "subagent_type": "Explore"}})],
        "m1",
        None,
        None,
    ));
    who.send(asked("srvtoolu_main", "m2", None, None));
    who.send(asked("srvtoolu_child", "c1", Some("toolu_agent"), None));
    who.send(answered("srvtoolu_child", redacted(), "c1", Some("toolu_agent"), None));
    let scout = who.runs()[0].agents().iter().find(|a| support::is_subagent(a)).cloned().expect("the scout");
    same(calls(&who).iter().map(|c| c.caller.clone()).collect::<Vec<_>>(), vec![None, scout.id.clone()], "who asked");
    same(states(&calls(&who)), vec!["consulting".to_string(), "done".to_string()], "their states");
    who.send(used("toolu_grep", "Grep", "c2", Some("toolu_agent")));
    same(calls(&who).iter().map(|c| c.followed).collect::<Vec<_>>(), vec![None, Some(true)], "each caller's work is its own");
    who.send(result("stopped early", false, None));
    same(states(&calls(&who)), vec!["stopped".to_string(), "done".to_string()], "the turn ended first");

    // A notice turn's consultation is kept on its own run, each turn's result gives its own run the cost.
    let mut notice = Pane::new("advised");
    notice.send(advised(
        vec![json!({"type": "tool_use", "id": "toolu_bg", "name": "Agent", "input": {"description": "count", "prompt": "Count.", "run_in_background": true}})],
        "m1",
        None,
        None,
    ));
    notice.send(tool_result(
        "toolu_bg",
        "Async agent launched successfully.\nagentId: agent1",
        false,
        Some(json!({"isAsync": true, "status": "async_launched", "agentId": "agent1"})),
        "tool_use_result",
    ));
    notice.send(asked("srvtoolu_request", "m2", None, None));
    notice.send(answered("srvtoolu_request", redacted(), "m2", None, None));
    notice.send(result(
        "launched.",
        false,
        Some(json!({"input_tokens": 4, "output_tokens": 9, "iterations": [advisor_message(1_000, 50)]})),
    ));
    notice.send(json!({"type": "system", "subtype": "task_notification", "task_id": "agent1", "tool_use_id": "toolu_bg", "status": "completed", "summary": "Done"}));
    notice.send(asked("srvtoolu_notice", "m3", None, None));
    notice.send(answered("srvtoolu_notice", redacted(), "m3", None, None));
    notice.send(result("done.", true, Some(json!({"input_tokens": 4, "output_tokens": 9, "iterations": [advisor_message(2_000, 70)]}))));
    let notice_runs = notice.finish("completed");
    same(notice_runs.iter().map(|r| r.is_notice()).collect::<Vec<_>>(), vec![false, true], "the request and its notice run");
    let request_advisor = notice_runs[0].advisor.clone().expect("the request's advisor");
    let notice_advisor = notice_runs[1].advisor.clone().expect("the notice run's advisor");
    same(ids(&consultations(&request_advisor)), vec!["srvtoolu_request".to_string()], "the request's");
    same(ids(&consultations(&notice_advisor)), vec!["srvtoolu_notice".to_string()], "the notice turn's");
    same(request_advisor.usage, Some(usage(1_000, 50, 0)), "the request's cost");
    same(notice_advisor.usage, Some(usage(2_000, 70, 0)), "the notice turn's cost");
    same(notice_advisor.model.as_deref(), Some("claude-opus-5-5"), "its model");

    // Still out when the process ends: stopped.
    let mut ended = Pane::new("advised");
    ended.send(advised(
        vec![json!({"type": "tool_use", "id": "toolu_agent", "name": "Agent", "input": {"description": "scout", "prompt": "Look."}})],
        "m1",
        None,
        None,
    ));
    ended.send(asked("srvtoolu_child", "c1", Some("toolu_agent"), Some("2026-11-10T10:00:00Z")));
    let stopped = consultations(&ended.finish("stopped")[0].advisor.clone().expect("an advisor"))[0].clone();
    check(stopped.state.as_deref() == Some("stopped") && stopped.ended_at.is_some(), "stopped with its end");
}

#[test]
fn the_advisors_tokens_model_bounds_and_saves() {
    let mut plain = Pane::new("advised");
    plain.send(result(
        "plain",
        false,
        Some(json!({"input_tokens": 4, "output_tokens": 9, "iterations": [{"type": "message", "input_tokens": 9, "output_tokens": 9}]})),
    ));
    same(plain.runs()[0].advisor.clone(), None, "a result without advisor messages adds nothing");
    let mut summed = Pane::new("advised");
    summed.send(result(
        "advised",
        false,
        Some(json!({"input_tokens": 4, "output_tokens": 9, "iterations": [advisor_message(100, 10),
            {"type": "message", "model": "x", "input_tokens": 9, "output_tokens": 9, "cache_read_input_tokens": 0, "cache_creation_input_tokens": 0},
            advisor_message(300, 30)]})),
    ));
    let advisor = summed.runs()[0].advisor.clone().expect("an advisor");
    same(advisor.usage, Some(usage(400, 40, 0)), "the advisor's messages alone");
    check(
        advisor.model.as_deref() == Some("claude-opus-5-5") && consultations(&advisor).is_empty() && count(&advisor) == 0,
        "its model and no consultation",
    );

    // A transcript names the advisor and its tokens on the message itself, each message once; and the larger of the two sources counts.
    let line = |content: Vec<Value>, id: &str, time: &str, model: Option<&str>, iterations: Vec<Value>| {
        let mut value = json!({"type": "assistant", "uuid": mighty_core::wire::new_id(), "timestamp": time, "requestedModel": "claude-sonnet-5-5",
            "message": {"id": id, "model": "claude-sonnet-5-5", "content": content,
                "usage": {"input_tokens": 4, "output_tokens": 177, "cache_read_input_tokens": 43_694, "iterations": iterations}}});
        if let Some(model) = model {
            value["advisorModel"] = json!(model);
        }
        value
    };
    let iterations = || {
        vec![
            json!({"type": "message", "input_tokens": 2, "output_tokens": 174}),
            advisor_message(34_217, 570),
            json!({"type": "message", "input_tokens": 2, "output_tokens": 3}),
        ]
    };
    let mut transcript = Pane::new("transcript");
    transcript.send(line(
        vec![json!({"type": "thinking", "thinking": ""})],
        "msg_1",
        "2026-11-10T18:16:51.656Z",
        Some("claude-opus-5-5"),
        iterations(),
    ));
    transcript.send(line(
        vec![json!({"type": "server_tool_use", "id": "srvtoolu_1", "name": "advisor", "input": {}})],
        "msg_1",
        "2026-11-10T18:16:51.658Z",
        Some("claude-opus-5-5"),
        iterations(),
    ));
    transcript.send(line(
        vec![json!({"type": "advisor_tool_result", "tool_use_id": "srvtoolu_1", "content": redacted()})],
        "msg_1",
        "2026-11-10T18:16:59.540Z",
        Some("claude-opus-5-5"),
        iterations(),
    ));
    transcript.send(line(
        vec![json!({"type": "text", "text": "No"})],
        "msg_1",
        "2026-11-10T18:16:59.541Z",
        Some("claude-opus-5-5"),
        iterations(),
    ));
    let read = transcript.finish("completed")[0].advisor.clone().expect("an advisor");
    check(read.model.as_deref() == Some("claude-opus-5-5") && read.usage == Some(usage(34_217, 570, 0)), "counted once");
    let read_calls = consultations(&read);
    check(
        read_calls.len() == 1
            && read_calls[0].state.as_deref() == Some("done")
            && read_calls[0].ended_at.as_deref() == Some("2026-11-10T18:16:59.540Z"),
        "the consultation",
    );
    let mut unasked = Pane::new("unasked");
    unasked.send(line(
        vec![json!({"type": "text", "text": "Done."})],
        "msg_1",
        "2026-11-10T18:17:00Z",
        Some("claude-opus-5-5"),
        vec![json!({"type": "message", "input_tokens": 2, "output_tokens": 3})],
    ));
    same(
        unasked.runs()[0].advisor.clone(),
        Some(GraphAdvisor { model: Some("claude-opus-5-5".into()), usage: None, total: None, calls: Some(vec![]) }),
        "on, never asked",
    );
    let mut none = Pane::new("none");
    none.send(line(
        vec![json!({"type": "text", "text": "Done."})],
        "msg_1",
        "2026-11-10T18:17:00Z",
        None,
        vec![json!({"type": "message", "input_tokens": 2, "output_tokens": 3})],
    ));
    same(none.runs()[0].advisor.clone(), None, "off says nothing");
    let mut twice = Pane::new("advised");
    twice.send(line(
        vec![json!({"type": "text", "text": "a"})],
        "msg_1",
        "2026-11-10T10:00:00Z",
        Some("claude-opus-5-5"),
        vec![advisor_message(100, 10)],
    ));
    twice.send(line(
        vec![json!({"type": "text", "text": "b"})],
        "msg_2",
        "2026-11-10T10:00:01Z",
        Some("claude-opus-5-5"),
        vec![advisor_message(300, 30)],
    ));
    twice.send(line(
        vec![json!({"type": "text", "text": "b2"})],
        "msg_2",
        "2026-11-10T10:00:02Z",
        Some("claude-opus-5-5"),
        vec![advisor_message(320, 32)],
    ));
    same(twice.runs()[0].advisor.clone().and_then(|a| a.usage), Some(usage(420, 42, 0)), "a message at its newest figure");
    twice.send(result("done", false, Some(json!({"input_tokens": 4, "output_tokens": 9, "iterations": [advisor_message(420, 42)]}))));
    same(twice.runs()[0].advisor.clone().and_then(|a| a.usage), Some(usage(420, 42, 0)), "the result says the same: not counted twice");

    // The newest consultations are kept, the count says all.
    let mut many = Pane::new("advised");
    for index in 0..40 {
        many.send(asked(&format!("srvtoolu_{index}"), &format!("m{index}"), None, None));
        many.send(answered(&format!("srvtoolu_{index}"), redacted(), &format!("m{index}"), None, None));
    }
    let kept = many.runs()[0].advisor.clone().expect("an advisor");
    let kept_calls = consultations(&kept);
    check(
        kept_calls.len() == 32 && kept_calls[0].id.as_deref() == Some("srvtoolu_8") && kept_calls[31].id.as_deref() == Some("srvtoolu_39"),
        "the newest 32",
    );
    check(count(&kept) == 40 && kept.total == Some(40), "the count says all");

    // A saved record is bounded and a restored consultation still out is stopped.
    let time = "2026-11-10T10:00:00Z";
    let record = GraphAdvisor {
        model: Some("not a model".into()),
        usage: Some(usage(5, 0, 0)),
        total: None,
        calls: Some(
            vec![
                GraphAdvisorCall {
                    started_at: Some(time.into()),
                    redacted: Some(false),
                    before_change: Some(false),
                    ..call("srvtoolu_a", "consulting")
                },
                call("srvtoolu_a", "done"),
                call("bad id", "done"),
                GraphAdvisorCall { caller: Some("not an id".into()), ..call("srvtoolu_b", "done") },
                call("srvtoolu_c", "thinking"),
                GraphAdvisorCall {
                    caller: Some("graph-abc".into()),
                    started_at: Some("yesterday".into()),
                    ended_at: Some(time.into()),
                    text: Some(String::new()),
                    error_code: Some("overloaded\nplease".into()),
                    followed: Some(true),
                    ..call("srvtoolu_d", "error")
                },
            ]
            .into_iter()
            .map(Some)
            .collect(),
        ),
    };
    let normal = normalize::advisor(&record, false).expect("nothing kept");
    same(normal.model.clone(), None, "a model that is no name");
    let normal_calls = consultations(&normal);
    same(ids(&normal_calls), vec!["srvtoolu_a".to_string(), "srvtoolu_d".to_string()], "what is kept");
    same(normal_calls[0].clone(), GraphAdvisorCall { started_at: Some(time.into()), ..call("srvtoolu_a", "consulting") }, "the first");
    check(
        normal_calls[1].started_at.is_none() && normal_calls[1].ended_at.as_deref() == Some(time) && normal_calls[1].text.is_none(),
        "the second's times and text",
    );
    check(
        normal_calls[1].error_code.as_deref() == Some("overloaded please")
            && normal_calls[1].followed == Some(true)
            && normal_calls[1].caller.as_deref() == Some("graph-abc"),
        "its error and caller",
    );
    check(normal.total == Some(6) && count(&normal) == 6, "six seen");
    same(
        states(&consultations(&normalize::advisor(&record, true).expect("restored"))),
        vec!["stopped".to_string(), "error".to_string()],
        "restored",
    );
    same(normalize::advisor(&GraphAdvisor { model: None, usage: None, total: None, calls: Some(vec![]) }, false), None, "nothing");
    // The record always writes its calls, as the Mac's decoder needs them, and one written without them reads back with none.
    check(
        GraphAdvisor { model: Some("claude-opus-5-5".into()), usage: None, total: None, calls: Some(vec![]) }
            .to_json()
            .contains("\"calls\":[]"),
        "an advisor without consultations writes an empty list",
    );
    same(
        GraphAdvisor::read(&parse(r#"{"model":"claude-opus-5-5"}"#)).expect("an advisor").calls.map(|c| c.len()),
        Some(0),
        "an advisor read without calls has none",
    );
    let mut run = graph_run("r1", "running");
    run.input = Some("go".into());
    run.provider = Some("claude".into());
    run.advisor = Some(record.clone());
    run.configured_advisor = Some("opus".into());
    let decoded = MightyGraphRun::read(&parse(&run.to_json())).expect("a run");
    check(
        decoded.advisor.as_ref() == Some(&record) && decoded.configured_advisor.as_deref() == Some("opus"),
        "a run carries it through a save",
    );
    let mut budget = 1_000_000;
    let restored = support::normalized(std::slice::from_ref(&run), true, &mut budget, None).remove(0);
    check(
        restored.advisor.as_ref().map(|a| states(&consultations(a))) == Some(vec!["stopped".into(), "error".into()])
            && restored.configured_advisor.as_deref() == Some("opus"),
        "restored",
    );
    run.configured_advisor = Some("not a model".into());
    same(
        support::normalized(std::slice::from_ref(&run), false, &mut budget, None).remove(0).configured_advisor,
        None,
        "a configured advisor that is no name",
    );
    let mut advised_run = graph_run("r3", "completed");
    advised_run.input = Some("go".into());
    advised_run.provider = Some("claude".into());
    advised_run.advisor = Some(GraphAdvisor {
        model: None,
        usage: None,
        total: None,
        calls: Some(vec![Some(GraphAdvisorCall { text: Some("x".repeat(900)), ..call("srvtoolu_t", "done") })]),
    });
    let mut small = 1_200;
    same(
        support::normalized(std::slice::from_ref(&advised_run), false, &mut small, None).remove(0).advisor,
        None,
        "no room: the record goes whole",
    );
    let mut roomy = 100_000;
    same(
        support::normalized(std::slice::from_ref(&advised_run), false, &mut roomy, None)
            .remove(0)
            .advisor
            .map(|a| consultations(&a)[0].text.clone().unwrap_or_default().len()),
        Some(900),
        "room: kept whole",
    );
    let mut sub = ExecutionGraphNode::new("graph-a".into(), "r1".into(), Some("graph-m".into()), "agent", "running", "a".into());
    sub.advisor = Some(record.clone());
    same(node::normalized(&sub, false).and_then(|n| n.advisor), None, "only a main block keeps one");
    let mut main = ExecutionGraphNode::new("graph-m".into(), "r1".into(), None, "main", "running", "Claude".into());
    main.advisor = Some(record);
    same(node::normalized(&main, false).and_then(|n| n.advisor), Some(normal), "a main block's");

    // A request remembers the advisor it was started with, and its notice runs do too; another agent has none.
    same(Pane::with("r1", None, Some("opus"), ko()).runs()[0].configured_advisor.as_deref(), Some("opus"), "the pane's model");
    same(Pane::with("r2", None, Some("not a model"), ko()).runs()[0].configured_advisor.clone(), None, "no name");
    let codex =
        ExecutionGraphTracker::new("c1", Some("go"), "codex", None, Box::new(|_: &ExecutionGraphNode| {}), None, Some("opus"), ko());
    same(codex.build_run(true).expect("a run").configured_advisor, None, "another agent");
    let mut learned = Pane::new("r3");
    learned.tracker.note_advisor(Some("sonnet"));
    same(learned.runs()[0].configured_advisor.as_deref(), Some("sonnet"), "learned once Claude's settings were read");
    learned.tracker.note_advisor(Some("not a model"));
    same(learned.runs()[0].configured_advisor.clone(), None, "a value that is no model");
}
