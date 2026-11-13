//! C# RuntimeParityVerification's child-graph checks (MightyCode.Core.Tests/RuntimeParityVerification.cs): a run's frames fed to the
//! tracker and to the run engine's stream parser together, as `RunManager` feeds them (its `Pipeline`, :76-101), and the Mods events to
//! both. Its stream halves (ApprovalStateWinsDelayedMods, ModsNeverReplaceRootResume) are mighty-run's (tests/stream_parser.rs).

use std::sync::{Arc, Mutex};

use mighty_core::graph::MightyGraphAgent;
use mighty_core::model::AgentActivity;
use mighty_graph::ExecutionGraphTracker;
use mighty_graph::tracker::ModMetadata;
use mighty_run::parser::{OutputParser, ParserHooks};
use serde_json::Value;

/// C# `RuntimeParityVerification.Pipeline`: the tracker and the parser of one run, the main agent's activity rows kept apart.
struct Pipeline {
    run_id: String,
    graph: Arc<Mutex<ExecutionGraphTracker>>,
    parser: OutputParser,
    main_activities: Arc<Mutex<Vec<AgentActivity>>>,
}

impl Pipeline {
    fn new() -> Pipeline {
        let run_id = format!("runtime-{}", mighty_core::wire::new_id());
        let graph = Arc::new(Mutex::new(ExecutionGraphTracker::new(
            &run_id,
            Some("Review the screenshots"),
            "claude",
            None,
            Box::new(|_| {}),
            None,
            None,
            mighty_core::locale::Locale::new("ko"),
        )));
        let main_activities = Arc::new(Mutex::new(vec![]));
        let (routed, rows) = (graph.clone(), main_activities.clone());
        let hooks = ParserHooks {
            activity: Some(Arc::new(move |value: AgentActivity| rows.lock().unwrap().push(value))),
            graph_activity: Some(Arc::new(move |value: &AgentActivity, tool: Option<&str>| routed.lock().unwrap().activity(value, tool))),
            ..ParserHooks::default()
        };
        let parser = OutputParser::new("claude", &run_id, hooks);
        Pipeline { run_id, graph, parser, main_activities }
    }

    fn frame(&self, frame: &str) {
        self.graph.lock().unwrap().consume(&serde_json::from_str(frame).unwrap());
        self.parser.parse(frame);
    }

    fn mod_event(&self, frame: &str) {
        let value: Value = serde_json::from_str(frame).unwrap();
        if let Some(metadata) = ModMetadata::from_event(&value) {
            self.graph.lock().unwrap().receive_mod(&metadata);
        }
        self.parser.receive_mod(&value);
    }

    fn child(&self, tool: &str) -> MightyGraphAgent {
        let id = mighty_graph::node::agent_node_id(&self.run_id, tool);
        self.graph.lock().unwrap().build_run(true).unwrap().agents().iter().find(|a| a.id() == id).cloned().unwrap()
    }

    fn child_activity(&self, tool: &str, name: &str) -> AgentActivity {
        let child = self.child(tool);
        let rows: Vec<&AgentActivity> =
            child.entries().iter().filter_map(|e| e.activity.as_ref()).filter(|a| a.tool_name.as_deref() == Some(name)).collect();
        assert_eq!(rows.len(), 1, "one {name} row in {tool}'s graph");
        rows[0].clone()
    }

    fn main_has(&self, names: &[&str]) -> bool {
        self.main_activities.lock().unwrap().iter().any(|a| a.tool_name.as_deref().is_some_and(|n| names.contains(&n)))
    }
}

/// "runtime parity: child activities stay in their graph" (ChildActivitiesStayInTheirGraph, RuntimeParityVerification.cs:103-126).
#[test]
fn child_activities_stay_in_their_graph() {
    let flow = Pipeline::new();
    flow.frame(r#"{"type":"assistant","message":{"content":[{"type":"tool_use","id":"spawn","name":"Agent","input":{"description":"Reviewer","prompt":"Review"}}]}}"#);
    flow.frame(r#"{"type":"assistant","parent_tool_use_id":"spawn","message":{"content":[{"type":"tool_use","id":"child-read","name":"Read","input":{"file_path":"README.md"}}]}}"#);
    assert!(!flow.main_has(&["Read"]), "A child tool is absent from main activity rows");
    assert_eq!(flow.child_activity("spawn", "Read").state, "running", "The parser routes the raw child tool ID to its graph owner");
    flow.frame(r#"{"type":"user","parent_tool_use_id":"spawn","message":{"content":[{"type":"tool_result","tool_use_id":"child-read","content":"Reviewed"}]}}"#);
    let read = flow.child_activity("spawn", "Read");
    assert!(read.state == "completed" && read.output.as_deref() == Some("Reviewed"), "A child result replaces the same graph activity");

    // Mods can arrive before the stream names a spawn: pending ownership must survive that order.
    flow.mod_event(r#"{"event":"tool.call","claudeSessionId":"child-mod-session","agentId":"mod-agent","toolUseId":"mod-read","tool":"Read","summary":"other.md","sequence":1}"#);
    assert!(!flow.main_has(&["Read"]), "A child whose spawn has not arrived never leaks a tool into main");
    flow.mod_event(
        r#"{"event":"agent.spawn","claudeSessionId":"root","agentId":"mod-agent","graph":{"version":1,"phase":"starting","agentId":"mod-agent","parentToolUseId":"mod-spawn","name":"Second reviewer"}}"#,
    );
    assert_eq!(flow.child_activity("mod-spawn", "Read").summary, "other.md", "A later spawn takes its pending tool activity");
    flow.mod_event(r#"{"event":"tool.complete","claudeSessionId":"child-mod-session","agentId":"mod-agent","toolUseId":"mod-read","tool":"Read","output":"Finished","sequence":2}"#);
    let read = flow.child_activity("mod-spawn", "Read");
    assert!(read.state == "completed" && read.output.as_deref() == Some("Finished"), "Mod completion stays with the child owner");

    flow.frame(r#"{"type":"assistant","parent_tool_use_id":"spawn","message":{"content":[{"type":"tool_use","id":"child-open","name":"Bash","input":{"command":"build"}}]}}"#);
    flow.parser.finish_activities(true);
    assert_eq!(
        flow.child_activity("spawn", "Bash").state,
        "stopped",
        "Finishing a child activity retains its already known owner without a new raw ID"
    );
    assert!(!flow.main_has(&["Read", "Bash"]), "Neither results nor finalization leak child rows into main");
}
