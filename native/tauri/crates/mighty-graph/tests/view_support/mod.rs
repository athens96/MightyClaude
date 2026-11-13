//! What the views' tests share: the shared fixtures, the C# models' defaults as builders, and the graph vectors' comparator
//! (GraphVectorVerification.Difference: numbers within 0.001, objects with exactly the expected keys).

#![allow(dead_code, reason = "each test file uses part of it")]

use std::path::{Path, PathBuf};

use mighty_core::graph::{GraphAgentFigures, GraphAgentIdentity, MightyGraphAgent, MightyGraphRun};
use mighty_core::locale::Locale;
use mighty_core::model::{AgentActivity, LogEntry};
use serde_json::Value;

pub fn ko() -> Locale {
    Locale::new("ko")
}

/// native/contracts.
pub fn contracts() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../contracts")
}

pub fn read_json(path: &Path) -> Value {
    let text = std::fs::read_to_string(path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    serde_json::from_str(&text).unwrap_or_else(|e| panic!("{}: {e}", path.display()))
}

/// C# `Check(value, message)`.
#[track_caller]
pub fn check(value: bool, message: impl AsRef<str>) {
    assert!(value, "{}", message.as_ref());
}

/// C# `new MightyGraphRun { Id, Input, Status }` with every list empty.
pub fn run(id: &str, input: &str, status: &str) -> MightyGraphRun {
    MightyGraphRun {
        id: Some(id.into()),
        input: Some(input.into()),
        status: Some(status.into()),
        root_entries: Some(vec![]),
        agents: Some(vec![]),
        result_entries: Some(vec![]),
        ..MightyGraphRun::default()
    }
}

/// C# `new MightyGraphAgent { Id, Title, Input, Status }`.
pub fn agent(id: &str, title: &str, input: &str, status: &str) -> MightyGraphAgent {
    MightyGraphAgent {
        id: Some(id.into()),
        title: Some(title.into()),
        input: Some(input.into()),
        status: Some(status.into()),
        entries: Some(vec![]),
        ..MightyGraphAgent::default()
    }
}

pub fn with_agents(mut run: MightyGraphRun, agents: Vec<MightyGraphAgent>) -> MightyGraphRun {
    run.agents = Some(agents);
    run
}

pub fn with_final(mut run: MightyGraphRun, output: &str) -> MightyGraphRun {
    run.final_output = Some(output.into());
    run
}

pub fn parent(mut agent: MightyGraphAgent, parent: &str) -> MightyGraphAgent {
    agent.parent_id = Some(parent.into());
    agent
}

pub fn kind(mut agent: MightyGraphAgent, kind: &str) -> MightyGraphAgent {
    agent.kind = Some(kind.into());
    agent
}

pub fn background(mut agent: MightyGraphAgent) -> MightyGraphAgent {
    agent.background = Some(true);
    agent
}

pub fn identity(kind: Option<&str>, requested: Option<&str>, resolved: Option<&str>, isolation: Option<&str>) -> GraphAgentIdentity {
    GraphAgentIdentity {
        kind: kind.map(str::to_string),
        requested_model: requested.map(str::to_string),
        resolved_model: resolved.map(str::to_string),
        isolation: isolation.map(str::to_string),
    }
}

pub fn figures(started: Option<&str>, ended: Option<&str>, tools: Option<i32>, exit: Option<i32>) -> GraphAgentFigures {
    GraphAgentFigures {
        started_at: started.map(str::to_string),
        ended_at: ended.map(str::to_string),
        tool_calls: tools,
        exit_code: exit,
    }
}

/// C# `new LogEntry(id, kind, text, timestamp, provider, activity)`.
pub fn entry(id: &str, kind: &str, text: &str, timestamp: &str, provider: Option<&str>) -> LogEntry {
    LogEntry::new(id.into(), kind, text.into(), timestamp.into(), provider.map(str::to_string))
}

pub fn with_activity(mut entry: LogEntry, activity: AgentActivity) -> LogEntry {
    entry.activity = Some(activity);
    entry
}

pub fn activity(id: &str, provider: &str, kind: &str, state: &str, summary: &str, tool: Option<&str>) -> AgentActivity {
    AgentActivity {
        id: id.into(),
        provider: provider.into(),
        kind: kind.into(),
        state: state.into(),
        summary: summary.into(),
        tool_name: tool.map(str::to_string),
        output: None,
        duration_ms: None,
    }
}

// ── FlowFixtures (C# FlowRolesVerification.cs:7): a moment as an offset from 1 800 000 000 seconds ──

pub mod flow {
    use std::collections::HashSet;

    use mighty_core::graph::{GraphAdvisor, GraphAgentFigures, GraphAgentIdentity, GraphResponseRecord, GraphTokenUsage, MightyGraphAgent, MightyGraphRun};
    use mighty_core::model::LogEntry;
    use mighty_core::time::{self, DateTimeOffset};
    use mighty_graph::layout::{self, FanOut, Layout, Node, Options};
    use mighty_graph::{node, support};

    use super::{activity, entry, ko};

    /// `Stamp`: `yyyy-MM-dd'T'HH:mm:ss'Z'`.
    pub fn stamp(seconds: i64) -> String {
        chrono::DateTime::from_timestamp(1_800_000_000 + seconds, 0).unwrap().format("%Y-%m-%dT%H:%M:%SZ").to_string()
    }

    /// `Date`.
    pub fn date(seconds: i64) -> DateTimeOffset {
        time::parse(Some(&stamp(seconds))).unwrap()
    }

    /// `Tool(id, summary, at, name, kind, state)`.
    pub fn tool(id: &str, summary: &str, at: i64, name: &str, kind: &str, state: &str) -> LogEntry {
        let mut e = entry(id, "system", summary, &stamp(at), Some("claude"));
        e.activity = Some(activity(id, "claude", kind, state, summary, Some(name)));
        e
    }

    /// `Tool(id, summary)` with its defaults: at 0, Read, a completed tool call.
    pub fn read(id: &str, summary: &str) -> LogEntry {
        tool(id, summary, 0, "Read", "tool", "completed")
    }

    /// `Said(id, text, at, kind)`.
    pub fn said(id: &str, text: &str, at: i64) -> LogEntry {
        entry(id, "assistant", text, &stamp(at), Some("claude"))
    }

    pub fn said_as(id: &str, text: &str, at: i64, kind: &str) -> LogEntry {
        entry(id, kind, text, &stamp(at), Some("claude"))
    }

    pub fn usage(input: i64, output: i64) -> GraphTokenUsage {
        GraphTokenUsage { input_tokens: input, output_tokens: output, ..GraphTokenUsage::default() }
    }

    /// `Answered(id, model)`.
    pub fn answered(id: &str, model: &str) -> GraphResponseRecord {
        node::response_record(id, Some(model), usage(10, 1), &[], false)
    }

    /// `FlowFixtures.Agent` with its named arguments.
    #[derive(Clone)]
    pub struct A {
        pub id: String,
        pub parent: Option<String>,
        pub status: String,
        pub background: bool,
        pub kind: Option<String>,
        pub source: Option<String>,
        pub title: Option<String>,
        pub input: String,
        pub entries: Vec<LogEntry>,
        pub identity: Option<GraphAgentIdentity>,
        pub figures: Option<GraphAgentFigures>,
        pub usage: Option<GraphTokenUsage>,
        pub records: Option<Vec<GraphResponseRecord>>,
    }

    pub fn a(id: &str) -> A {
        A {
            id: id.into(),
            parent: None,
            status: "running".into(),
            background: false,
            kind: None,
            source: None,
            title: None,
            input: "확인".into(),
            entries: vec![],
            identity: None,
            figures: None,
            usage: None,
            records: None,
        }
    }

    impl A {
        pub fn parent(mut self, p: &str) -> Self {
            self.parent = Some(p.into());
            self
        }
        pub fn status(mut self, s: &str) -> Self {
            self.status = s.into();
            self
        }
        pub fn background(mut self) -> Self {
            self.background = true;
            self
        }
        pub fn kind(mut self, k: &str) -> Self {
            self.kind = Some(k.into());
            self
        }
        pub fn source(mut self, s: &str) -> Self {
            self.source = Some(s.into());
            self
        }
        pub fn title(mut self, t: &str) -> Self {
            self.title = Some(t.into());
            self
        }
        pub fn input(mut self, i: &str) -> Self {
            self.input = i.into();
            self
        }
        pub fn entries(mut self, e: Vec<LogEntry>) -> Self {
            self.entries = e;
            self
        }
        pub fn identity(mut self, i: GraphAgentIdentity) -> Self {
            self.identity = Some(i);
            self
        }
        pub fn figures(mut self, f: GraphAgentFigures) -> Self {
            self.figures = Some(f);
            self
        }
        pub fn usage(mut self, u: GraphTokenUsage) -> Self {
            self.usage = Some(u);
            self
        }
        pub fn records(mut self, r: Vec<GraphResponseRecord>) -> Self {
            self.records = Some(r);
            self
        }
        pub fn build(self) -> MightyGraphAgent {
            MightyGraphAgent {
                title: Some(self.title.clone().unwrap_or_else(|| self.id.clone())),
                id: Some(self.id),
                parent_id: self.parent,
                input: Some(self.input),
                status: Some(self.status),
                kind: self.kind,
                background: self.background.then_some(true),
                source_block_id: self.source,
                entries: Some(self.entries),
                identity: self.identity.filter(|i| *i != GraphAgentIdentity::default()),
                figures: self.figures.filter(|f| *f != GraphAgentFigures::default()),
                usage: self.usage,
                response_records: self.records.map(|r| r.into_iter().map(Some).collect()),
                ..MightyGraphAgent::default()
            }
        }
    }

    /// `FlowFixtures.Run` with its named arguments.
    #[derive(Clone)]
    pub struct R {
        pub id: String,
        pub status: String,
        pub agents: Vec<MightyGraphAgent>,
        pub root: Vec<LogEntry>,
        pub output: Option<String>,
        pub notice: bool,
        pub title: Option<String>,
        pub model: Option<String>,
        pub provider: String,
        pub advisor: Option<GraphAdvisor>,
        pub configured_advisor: Option<String>,
        pub effort: Option<String>,
    }

    pub fn r(id: &str) -> R {
        R {
            id: id.into(),
            status: "running".into(),
            agents: vec![],
            root: vec![],
            output: None,
            notice: false,
            title: None,
            model: Some("claude-sonnet-5-5".into()),
            provider: "claude".into(),
            advisor: None,
            configured_advisor: None,
            effort: None,
        }
    }

    impl R {
        pub fn status(mut self, s: &str) -> Self {
            self.status = s.into();
            self
        }
        pub fn agents(mut self, a: Vec<MightyGraphAgent>) -> Self {
            self.agents = a;
            self
        }
        pub fn root(mut self, r: Vec<LogEntry>) -> Self {
            self.root = r;
            self
        }
        pub fn output(mut self, o: &str) -> Self {
            self.output = Some(o.into());
            self
        }
        pub fn notice(mut self) -> Self {
            self.notice = true;
            self
        }
        pub fn title(mut self, t: &str) -> Self {
            self.title = Some(t.into());
            self
        }
        pub fn model(mut self, m: Option<&str>) -> Self {
            self.model = m.map(str::to_string);
            self
        }
        pub fn provider(mut self, p: &str) -> Self {
            self.provider = p.into();
            self
        }
        pub fn advisor(mut self, a: GraphAdvisor) -> Self {
            self.advisor = Some(a);
            self
        }
        pub fn effort(mut self, e: &str) -> Self {
            self.effort = Some(e.into());
            self
        }
        pub fn build(self) -> MightyGraphRun {
            let mut run = MightyGraphRun {
                input: Some(format!("요청 {}", self.id)),
                response_records: self.model.as_ref().map(|m| vec![Some(node::response_record(&format!("{}-m", self.id), Some(m), usage(1, 1), &[], false))]),
                id: Some(self.id),
                status: Some(self.status),
                agents: Some(self.agents),
                root_entries: Some(self.root),
                result_entries: Some(vec![]),
                final_output: self.output,
                provider: Some(self.provider),
                notice_run: self.notice.then_some(true),
                notice_title: self.title,
                advisor: self.advisor,
                configured_advisor: self.configured_advisor,
                configured_effort: self.effort,
                ..MightyGraphRun::default()
            };
            support::refresh_result(&mut run);
            run
        }
    }

    /// `FlowFixtures.Layout(runs, running, opened, fanOut, expanded, sizes, plan)`: the role view of the shown runs.
    pub fn flow_layout(runs: &[MightyGraphRun], running: bool, opened: &[String], fan_out: bool) -> Layout {
        flow_layout_with(runs, running, opened, fan_out, &[], None, None)
    }

    pub fn flow_layout_with(
        runs: &[MightyGraphRun],
        running: bool,
        opened: &[String],
        fan_out: bool,
        expanded: &[String],
        sizes: Option<std::collections::HashMap<String, mighty_core::graph::GraphBlockSize>>,
        plan: Option<&str>,
    ) -> Layout {
        let shown = support::shown_runs(runs, &ko());
        let options = Options {
            block_sizes: sizes,
            plan_run_id: plan.map(str::to_string),
            splits_execution: true,
            fan_out: fan_out.then(|| FanOut::opened(opened.iter().cloned())),
            ..Options::default()
        };
        let expanded: HashSet<String> = expanded.iter().cloned().collect();
        layout::make(&shown, "", running, &expanded, &options, &ko())
    }

    /// `Node(run, suffix)`.
    pub fn node_of(run: &MightyGraphRun, suffix: &str) -> String {
        layout::node_id(run, suffix)
    }

    /// `Find`, `Must`.
    pub fn find<'a>(layout: &'a Layout, id: &str) -> Option<&'a Node> {
        layout.nodes.iter().find(|n| n.id == id)
    }
    pub fn must<'a>(layout: &'a Layout, id: &str) -> &'a Node {
        find(layout, id).unwrap_or_else(|| panic!("no node {id}"))
    }

    /// `Overlap`: two frames share area.
    pub fn overlap(a: &mighty_graph::camera::GraphRect, b: &mighty_graph::camera::GraphRect) -> bool {
        a.x < b.max_x() && b.x < a.max_x() && a.y < b.max_y() && b.y < a.max_y()
    }
}

/// C# `Same(actual, expected, why)`.
#[track_caller]
pub fn same<T: PartialEq + std::fmt::Debug>(actual: T, expected: T, why: &str) {
    assert!(actual == expected, "{why}: expected <{expected:?}> but was <{actual:?}>");
}

/// GraphVectorVerification.Difference: the first place `produced` differs from the committed expectation.
pub fn difference(expected: &Value, produced: &Value, path: &str) -> Option<String> {
    match expected {
        Value::Null => (!produced.is_null()).then(|| format!("{path}: expected null, produced {produced}")),
        Value::Bool(flag) => (produced.as_bool() != Some(*flag)).then(|| format!("{path}: expected {flag}, produced {produced}")),
        Value::Number(number) => match produced.as_f64() {
            Some(value) if (number.as_f64().unwrap_or(f64::NAN) - value).abs() <= 0.001 => None,
            _ => Some(format!("{path}: expected {number}, produced {produced}")),
        },
        Value::String(text) => (produced.as_str() != Some(text)).then(|| format!("{path}: expected {text:?}, produced {produced}")),
        Value::Array(items) => {
            let Some(list) = produced.as_array() else { return Some(format!("{path}: expected an array, produced {produced}")) };
            if list.len() != items.len() {
                return Some(format!("{path}: expected {} items, produced {}", items.len(), list.len()));
            }
            items.iter().zip(list).enumerate().find_map(|(i, (e, p))| difference(e, p, &format!("{path}[{i}]")))
        }
        Value::Object(map) => {
            let Some(made) = produced.as_object() else { return Some(format!("{path}: expected an object, produced {produced}")) };
            let mut keys: Vec<&String> = map.keys().collect();
            let mut have: Vec<&String> = made.keys().collect();
            keys.sort();
            have.sort();
            if keys != have {
                return Some(format!("{path}: expected keys {keys:?}, produced {have:?}"));
            }
            map.iter().find_map(|(k, v)| difference(v, &made[k], &format!("{path}.{k}")))
        }
    }
}
