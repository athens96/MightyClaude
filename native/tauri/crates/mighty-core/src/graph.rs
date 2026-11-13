//! A pane's Mighty graph runs as saved: C# `MightyGraphRun`, `MightyGraphAgent`, `GraphTokenUsage`, `GraphResponseRecord`,
//! `GraphAgentIdentity`, `GraphAgentFigures`, `GraphAdvisor`, `GraphAdvisorCall`, `FlowStep`, `GraphBlockSize`
//! (MightyCode.Core/MightyGraph.cs, ExecutionGraph.cs, FlowSteps.cs), read and written as `Wire.Json` does, and the pure rules of
//! `MightyGraphSupport` and `BackgroundNoticeRuns` the state's normalize reads.
//!
//! A null inside `agents`, `rootEntries`, an agent's `entries`, `steps` or `stepsCarried` fails the read: C# reads it and then
//! throws (`NullReferenceException`) as soon as Normalize walks that pane, which fails the load as well. (C# does not fail when the
//! pane holding it is dropped before its runs are walked; this read fails all the same.)

use serde_json::Value;

use crate::json::{self, JsonOut, Obj, R, Wire};
use crate::model::{LogEntry, write_nullable_list, write_nullable_strings};

/// `GraphBlockPreferences.SplitLayout`.
pub const SPLIT_LAYOUT: i32 = 1;

/// `MightyGraphSupport.Providers`.
pub const GRAPH_PROVIDERS: [&str; 6] = ["claude", "codex", "gemini", "pi", "opencode", "hermes"];

/// `MightyGraphSupport.Terminal`.
pub fn terminal(status: &str) -> bool {
    matches!(status, "completed" | "error" | "stopped")
}

#[derive(Debug, Clone, PartialEq, Default)]
pub struct MightyGraphRun {
    pub id: Option<String>,
    pub input: Option<String>,
    pub status: Option<String>,
    pub root_entries: Option<Vec<LogEntry>>,
    pub agents: Option<Vec<MightyGraphAgent>>,
    pub result_entries: Option<Vec<LogEntry>>,
    pub source_run_id: Option<String>,
    pub final_output: Option<String>,
    pub usage: Option<GraphTokenUsage>,
    pub response_records: Option<Vec<Option<GraphResponseRecord>>>,
    pub provider: Option<String>,
    pub node_model_label: Option<String>,
    pub configured_model: Option<String>,
    pub notice_run: Option<bool>,
    pub notice_title: Option<String>,
    pub configured_effort: Option<String>,
    pub advisor: Option<GraphAdvisor>,
    pub configured_advisor: Option<String>,
    pub steps: Option<Vec<FlowStep>>,
    pub steps_carried: Option<Vec<String>>,
    /// `[JsonIgnore] ShownFanOut`: the background blocks `MightyGraphRun.Shown` took out of `agents`, with the blocks under them, which
    /// the flow diagram alone still draws. Never read or written.
    pub shown_fan_out: Option<Vec<MightyGraphAgent>>,
    /// `[JsonIgnore] ShownFlowOrder`: where each of the flow's agents stood among the run's blocks before `Shown`. Never read or written.
    pub shown_flow_order: Option<Vec<usize>>,
    /// `[JsonIgnore] ShownBackgroundWaiting`: `BackgroundWaiting` of the run `Shown` was made from. Never read or written.
    pub shown_background_waiting: Option<i32>,
    /// `[JsonIgnore] RequestBackgroundWaiting`: the background work of the whole request (`WithBackgroundWaiting`). Never read or written.
    pub request_background_waiting: Option<i32>,
    /// `[JsonIgnore] ShownAwaiting`: `BackgroundNoticeRuns.AwaitingNotice` of the run `Shown` was made from. Never read or written.
    pub shown_awaiting: Option<Awaiting>,
}

/// C# `BackgroundNoticeRuns.AwaitedTask(Id, Title, Status)`: one background task or sub-agent of a request awaiting its notices.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AwaitedTask {
    pub id: String,
    pub title: String,
    pub status: String,
}

impl AwaitedTask {
    /// `Delivered`: the work ended (and told Claude so), whatever its outcome.
    pub fn delivered(&self) -> bool {
        terminal(&self.status)
    }
}

/// C# `BackgroundNoticeRuns.Awaiting(Tasks)`: a request awaiting the notices of the background work it started.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Awaiting {
    pub tasks: Vec<AwaitedTask>,
}

impl Awaiting {
    /// `Delivered`: how many of its tasks' notices are in.
    pub fn delivered(&self) -> usize {
        self.tasks.iter().filter(|t| t.delivered()).count()
    }
}

fn text_or(value: Option<&Value>, default: &str) -> R<Option<String>> {
    json::string(value, || Some(default.to_string()))
}

fn entries(value: Option<&Value>, what: &str) -> R<Option<Vec<LogEntry>>> {
    match value {
        None => Ok(Some(vec![])),
        value => json::strict_list(value, what),
    }
}

impl Wire for MightyGraphRun {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a graph run")?;
        Ok(MightyGraphRun {
            id: text_or(o.get("id"), "")?,
            input: text_or(o.get("input"), "")?,
            status: text_or(o.get("status"), "running")?,
            root_entries: entries(o.get("rootEntries"), "root entry")?,
            agents: match o.get("agents") {
                None => Some(vec![]),
                value => json::strict_list(value, "graph agent")?,
            },
            result_entries: match o.get("resultEntries") {
                None => Some(vec![]),
                // Never read: Normalize makes the run's result anew.
                value => json::skipping_list(value)?,
            },
            source_run_id: json::opt_string(o.get("sourceRunID"))?,
            final_output: json::opt_string(o.get("finalOutput"))?,
            usage: json::record(o.get("usage"))?,
            response_records: json::list(o.get("responseRecords"), GraphResponseRecord::read)?,
            provider: json::opt_string(o.get("provider"))?,
            node_model_label: json::opt_string(o.get("nodeModelLabel"))?,
            configured_model: json::opt_string(o.get("configuredModel"))?,
            notice_run: json::opt_boolean(o.get("noticeRun"))?,
            notice_title: json::opt_string(o.get("noticeTitle"))?,
            configured_effort: json::opt_string(o.get("configuredEffort"))?,
            advisor: json::record(o.get("advisor"))?,
            configured_advisor: json::opt_string(o.get("configuredAdvisor"))?,
            steps: json::strict_list(o.get("steps"), "flow step")?,
            steps_carried: match json::string_list(o.get("stepsCarried"))? {
                None => None,
                Some(keys) => {
                    Some(keys.into_iter().map(|k| k.ok_or_else(|| json::ReadError::crash("a null carried step"))).collect::<R<_>>()?)
                }
            },
            shown_fan_out: None,
            shown_flow_order: None,
            shown_background_waiting: None,
            request_background_waiting: None,
            shown_awaiting: None,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        self.write_with(out, true);
    }
}

impl MightyGraphRun {
    /// Written with or without its agents' output files (`WebDelta.PageRun` leaves them on the host's side).
    pub fn write_with(&self, out: &mut JsonOut, output_files: bool) {
        out.begin_object();
        out.prop_str("id", self.id.as_deref());
        out.prop_str("input", self.input.as_deref());
        out.prop_str("status", self.status.as_deref());
        out.prop_list("rootEntries", self.root_entries.as_ref());
        if let Some(agents) = &self.agents {
            out.name("agents");
            out.begin_array();
            for agent in agents {
                agent.write_with(out, output_files);
            }
            out.end_array();
        }
        out.prop_list("resultEntries", self.result_entries.as_ref());
        out.prop_str("sourceRunID", self.source_run_id.as_deref());
        out.prop_str("finalOutput", self.final_output.as_deref());
        out.prop("usage", self.usage.as_ref());
        if let Some(records) = &self.response_records {
            out.name("responseRecords");
            write_nullable_list(out, records);
        }
        out.prop_str("provider", self.provider.as_deref());
        out.prop_str("nodeModelLabel", self.node_model_label.as_deref());
        out.prop_str("configuredModel", self.configured_model.as_deref());
        out.prop_opt_bool("noticeRun", self.notice_run);
        out.prop_str("noticeTitle", self.notice_title.as_deref());
        out.prop_str("configuredEffort", self.configured_effort.as_deref());
        out.prop("advisor", self.advisor.as_ref());
        out.prop_str("configuredAdvisor", self.configured_advisor.as_deref());
        out.prop_list("steps", self.steps.as_ref());
        out.prop_strings("stepsCarried", self.steps_carried.as_ref());
        out.end_object();
    }

    pub fn id(&self) -> &str {
        self.id.as_deref().unwrap_or("")
    }
    pub fn status(&self) -> &str {
        self.status.as_deref().unwrap_or("")
    }
    pub fn agents(&self) -> &[MightyGraphAgent] {
        self.agents.as_deref().unwrap_or(&[])
    }
    pub fn root_entries(&self) -> &[LogEntry] {
        self.root_entries.as_deref().unwrap_or(&[])
    }
    pub fn result_entries(&self) -> &[LogEntry] {
        self.result_entries.as_deref().unwrap_or(&[])
    }
    pub fn input(&self) -> &str {
        self.input.as_deref().unwrap_or("")
    }
    pub fn final_output(&self) -> Option<&str> {
        self.final_output.as_deref()
    }
    pub fn provider(&self) -> Option<&str> {
        self.provider.as_deref()
    }
    pub fn is_notice(&self) -> bool {
        self.notice_run == Some(true)
    }

    /// `Settled`: ended, and every block a view draws for it too.
    pub fn settled(&self) -> bool {
        terminal(self.status()) && shown_agents(self).iter().all(|a| terminal(a.status()))
    }

    /// `ReopenedWithReply`.
    pub fn reopened_with_reply(&self) -> bool {
        self.is_notice() && !terminal(self.status()) && self.final_output.as_deref().is_some_and(|f| !f.is_empty())
    }

    /// Whether an agent of the run keeps its output file (`WebDelta.PageRun` copies the run without them).
    pub fn has_output_files(&self) -> bool {
        self.agents().iter().any(|a| a.output_file.is_some())
    }
}

/// `BackgroundNoticeRuns.ShownAgents`: background blocks only inside a notice run; blocks under a hidden one are hidden with it.
pub fn shown_agents(run: &MightyGraphRun) -> Vec<&MightyGraphAgent> {
    let agents = run.agents();
    if run.is_notice() || !agents.iter().any(|a| a.background == Some(true)) {
        return agents.iter().collect();
    }
    let mut hidden: std::collections::HashSet<&str> = agents.iter().filter(|a| a.background == Some(true)).map(|a| a.id()).collect();
    let mut grew = true;
    while grew {
        grew = false;
        for agent in agents {
            if !hidden.contains(agent.id())
                && let Some(parent) = agent.parent_id.as_deref()
                && hidden.contains(parent)
            {
                hidden.insert(agent.id());
                grew = true;
            }
        }
    }
    agents.iter().filter(|a| !hidden.contains(a.id())).collect()
}

#[derive(Debug, Clone, PartialEq, Default)]
pub struct MightyGraphAgent {
    pub id: Option<String>,
    pub parent_id: Option<String>,
    pub title: Option<String>,
    pub input: Option<String>,
    pub status: Option<String>,
    pub entries: Option<Vec<LogEntry>>,
    pub kind: Option<String>,
    pub usage: Option<GraphTokenUsage>,
    pub activity_generation: Option<i32>,
    pub response_records: Option<Vec<Option<GraphResponseRecord>>>,
    pub background: Option<bool>,
    pub output_file: Option<String>,
    pub notice_reply: Option<String>,
    pub source_block_id: Option<String>,
    pub identity: Option<GraphAgentIdentity>,
    pub figures: Option<GraphAgentFigures>,
}

impl Wire for MightyGraphAgent {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a graph agent")?;
        Ok(MightyGraphAgent {
            id: text_or(o.get("id"), "")?,
            parent_id: json::opt_string(o.get("parentID"))?,
            title: text_or(o.get("title"), "")?,
            input: text_or(o.get("input"), "")?,
            status: text_or(o.get("status"), "running")?,
            entries: entries(o.get("entries"), "agent entry")?,
            kind: json::opt_string(o.get("kind"))?,
            usage: json::record(o.get("usage"))?,
            activity_generation: json::opt_int32(o.get("activityGeneration"))?,
            response_records: json::list(o.get("responseRecords"), GraphResponseRecord::read)?,
            background: json::opt_boolean(o.get("background"))?,
            output_file: json::opt_string(o.get("outputFile"))?,
            notice_reply: json::opt_string(o.get("noticeReply"))?,
            source_block_id: json::opt_string(o.get("sourceBlockId"))?,
            identity: json::record(o.get("identity"))?,
            figures: json::record(o.get("figures"))?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        self.write_with(out, true);
    }
}

impl MightyGraphAgent {
    pub fn write_with(&self, out: &mut JsonOut, output_file: bool) {
        out.begin_object();
        out.prop_str("id", self.id.as_deref());
        out.prop_str("parentID", self.parent_id.as_deref());
        out.prop_str("title", self.title.as_deref());
        out.prop_str("input", self.input.as_deref());
        out.prop_str("status", self.status.as_deref());
        out.prop_list("entries", self.entries.as_ref());
        out.prop_str("kind", self.kind.as_deref());
        out.prop("usage", self.usage.as_ref());
        out.prop_opt_int("activityGeneration", self.activity_generation.map(i64::from));
        if let Some(records) = &self.response_records {
            out.name("responseRecords");
            write_nullable_list(out, records);
        }
        out.prop_opt_bool("background", self.background);
        if output_file {
            out.prop_str("outputFile", self.output_file.as_deref());
        }
        out.prop_str("noticeReply", self.notice_reply.as_deref());
        out.prop_str("sourceBlockId", self.source_block_id.as_deref());
        out.prop("identity", self.identity.as_ref());
        out.prop("figures", self.figures.as_ref());
        out.end_object();
    }

    pub fn id(&self) -> &str {
        self.id.as_deref().unwrap_or("")
    }
    pub fn status(&self) -> &str {
        self.status.as_deref().unwrap_or("")
    }
    pub fn entries(&self) -> &[LogEntry] {
        self.entries.as_deref().unwrap_or(&[])
    }
    pub fn title(&self) -> &str {
        self.title.as_deref().unwrap_or("")
    }
    pub fn input(&self) -> &str {
        self.input.as_deref().unwrap_or("")
    }
    pub fn kind(&self) -> Option<&str> {
        self.kind.as_deref()
    }
    pub fn parent_id(&self) -> Option<&str> {
        self.parent_id.as_deref()
    }
    /// `IsTask`.
    pub fn is_task(&self) -> bool {
        self.kind.as_deref() == Some("task")
    }
    /// `IsBackgroundAgent`: a sub-agent started with `run_in_background`.
    pub fn is_background_agent(&self) -> bool {
        self.kind.is_none() && self.background == Some(true)
    }
}

/// C# `GraphTokenUsage(InputTokens = 0, OutputTokens = 0, CacheReadTokens = 0, CacheCreationTokens = 0)`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct GraphTokenUsage {
    pub input_tokens: i64,
    pub output_tokens: i64,
    pub cache_read_tokens: i64,
    pub cache_creation_tokens: i64,
}

impl GraphTokenUsage {
    pub const MAXIMUM_TOKENS: i64 = 1_000_000_000_000;

    pub fn total(&self) -> i64 {
        self.input_tokens.wrapping_add(self.output_tokens).wrapping_add(self.cache_read_tokens).wrapping_add(self.cache_creation_tokens)
    }

    /// `Normalized`: every counter in range and a positive total, else null.
    pub fn normalized(&self) -> Option<GraphTokenUsage> {
        let counters = [self.input_tokens, self.output_tokens, self.cache_read_tokens, self.cache_creation_tokens];
        if counters.iter().any(|v| *v < 0 || *v > Self::MAXIMUM_TOKENS) {
            return None;
        }
        (self.total() > 0).then_some(*self)
    }
}

impl Wire for GraphTokenUsage {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a token usage")?;
        Ok(GraphTokenUsage {
            input_tokens: json::int64(o.get("inputTokens"), 0)?,
            output_tokens: json::int64(o.get("outputTokens"), 0)?,
            cache_read_tokens: json::int64(o.get("cacheReadTokens"), 0)?,
            cache_creation_tokens: json::int64(o.get("cacheCreationTokens"), 0)?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_int("inputTokens", self.input_tokens);
        out.prop_int("outputTokens", self.output_tokens);
        out.prop_int("cacheReadTokens", self.cache_read_tokens);
        out.prop_int("cacheCreationTokens", self.cache_creation_tokens);
        out.end_object();
    }
}

/// C# `GraphResponseRecord(ResponseId, Model?, Usage, ActivityIds, MarkedAsConfigured = false)`, kept as read.
#[derive(Debug, Clone, PartialEq)]
pub struct GraphResponseRecord {
    pub response_id: Option<String>,
    pub model: Option<String>,
    pub usage: Option<GraphTokenUsage>,
    pub activity_ids: Option<Vec<Option<String>>>,
    pub marked_as_configured: bool,
}

impl Wire for GraphResponseRecord {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a response record")?;
        Ok(GraphResponseRecord {
            response_id: json::opt_string(o.get("responseId"))?,
            model: json::opt_string(o.get("model"))?,
            usage: json::record(o.get("usage"))?,
            activity_ids: json::string_list(o.get("activityIds"))?,
            marked_as_configured: json::boolean(o.get("markedAsConfigured"), false)?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("responseId", self.response_id.as_deref());
        out.prop_str("model", self.model.as_deref());
        out.prop("usage", self.usage.as_ref());
        if let Some(ids) = &self.activity_ids {
            out.name("activityIds");
            write_nullable_strings(out, ids);
        }
        out.prop_bool("markedAsConfigured", self.marked_as_configured);
        out.end_object();
    }
}

/// C# `GraphAgentIdentity(Type?, RequestedModel?, ResolvedModel?, Isolation?)`.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct GraphAgentIdentity {
    pub kind: Option<String>,
    pub requested_model: Option<String>,
    pub resolved_model: Option<String>,
    pub isolation: Option<String>,
}

impl Wire for GraphAgentIdentity {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "an agent identity")?;
        Ok(GraphAgentIdentity {
            kind: json::opt_string(o.get("type"))?,
            requested_model: json::opt_string(o.get("requestedModel"))?,
            resolved_model: json::opt_string(o.get("resolvedModel"))?,
            isolation: json::opt_string(o.get("isolation"))?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("type", self.kind.as_deref());
        out.prop_str("requestedModel", self.requested_model.as_deref());
        out.prop_str("resolvedModel", self.resolved_model.as_deref());
        out.prop_str("isolation", self.isolation.as_deref());
        out.end_object();
    }
}

/// C# `GraphAgentFigures(StartedAt?, EndedAt?, ToolCalls?, ExitCode?)`.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct GraphAgentFigures {
    pub started_at: Option<String>,
    pub ended_at: Option<String>,
    pub tool_calls: Option<i32>,
    pub exit_code: Option<i32>,
}

impl Wire for GraphAgentFigures {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "agent figures")?;
        Ok(GraphAgentFigures {
            started_at: json::opt_string(o.get("startedAt"))?,
            ended_at: json::opt_string(o.get("endedAt"))?,
            tool_calls: json::opt_int32(o.get("toolCalls"))?,
            exit_code: json::opt_int32(o.get("exitCode"))?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("startedAt", self.started_at.as_deref());
        out.prop_str("endedAt", self.ended_at.as_deref());
        out.prop_opt_int("toolCalls", self.tool_calls.map(i64::from));
        out.prop_opt_int("exitCode", self.exit_code.map(i64::from));
        out.end_object();
    }
}

/// C# `GraphAdvisorCall(Id, Caller?, StartedAt?, EndedAt?, State = "consulting", Redacted?, Text?, ErrorCode?, BeforeChange?,
/// Followed?)`.
#[derive(Debug, Clone, PartialEq)]
pub struct GraphAdvisorCall {
    pub id: Option<String>,
    pub caller: Option<String>,
    pub started_at: Option<String>,
    pub ended_at: Option<String>,
    pub state: Option<String>,
    pub redacted: Option<bool>,
    pub text: Option<String>,
    pub error_code: Option<String>,
    pub before_change: Option<bool>,
    pub followed: Option<bool>,
}

impl Wire for GraphAdvisorCall {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "an advisor call")?;
        Ok(GraphAdvisorCall {
            id: json::opt_string(o.get("id"))?,
            caller: json::opt_string(o.get("caller"))?,
            started_at: json::opt_string(o.get("startedAt"))?,
            ended_at: json::opt_string(o.get("endedAt"))?,
            state: text_or(o.get("state"), "consulting")?,
            redacted: json::opt_boolean(o.get("redacted"))?,
            text: json::opt_string(o.get("text"))?,
            error_code: json::opt_string(o.get("errorCode"))?,
            before_change: json::opt_boolean(o.get("beforeChange"))?,
            followed: json::opt_boolean(o.get("followed"))?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("id", self.id.as_deref());
        out.prop_str("caller", self.caller.as_deref());
        out.prop_str("startedAt", self.started_at.as_deref());
        out.prop_str("endedAt", self.ended_at.as_deref());
        out.prop_str("state", self.state.as_deref());
        out.prop_opt_bool("redacted", self.redacted);
        out.prop_str("text", self.text.as_deref());
        out.prop_str("errorCode", self.error_code.as_deref());
        out.prop_opt_bool("beforeChange", self.before_change);
        out.prop_opt_bool("followed", self.followed);
        out.end_object();
    }
}

/// C# `GraphAdvisor(Model?, Usage?, Calls?, Total?)`: its `calls` is never null (`Calls ?? []`) and is written after `total`.
#[derive(Debug, Clone, PartialEq)]
pub struct GraphAdvisor {
    pub model: Option<String>,
    pub usage: Option<GraphTokenUsage>,
    pub total: Option<i32>,
    pub calls: Option<Vec<Option<GraphAdvisorCall>>>,
}

impl Wire for GraphAdvisor {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "an advisor record")?;
        Ok(GraphAdvisor {
            model: json::opt_string(o.get("model"))?,
            usage: json::record(o.get("usage"))?,
            total: json::opt_int32(o.get("total"))?,
            calls: Some(json::list(o.get("calls"), GraphAdvisorCall::read)?.unwrap_or_default()),
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("model", self.model.as_deref());
        out.prop("usage", self.usage.as_ref());
        out.prop_opt_int("total", self.total.map(i64::from));
        if let Some(calls) = &self.calls {
            out.name("calls");
            write_nullable_list(out, calls);
        }
        out.end_object();
    }
}

/// C# `FlowStep(Key, Content, ActiveForm?, Status = "pending", StartedAt?, EndedAt?)`.
#[derive(Debug, Clone, PartialEq)]
pub struct FlowStep {
    pub key: Option<String>,
    pub content: Option<String>,
    pub active_form: Option<String>,
    pub status: Option<String>,
    pub started_at: Option<String>,
    pub ended_at: Option<String>,
}

impl Wire for FlowStep {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a flow step")?;
        Ok(FlowStep {
            key: json::opt_string(o.get("key"))?,
            content: json::opt_string(o.get("content"))?,
            active_form: json::opt_string(o.get("activeForm"))?,
            status: text_or(o.get("status"), "pending")?,
            started_at: json::opt_string(o.get("startedAt"))?,
            ended_at: json::opt_string(o.get("endedAt"))?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("key", self.key.as_deref());
        out.prop_str("content", self.content.as_deref());
        out.prop_str("activeForm", self.active_form.as_deref());
        out.prop_str("status", self.status.as_deref());
        out.prop_str("startedAt", self.started_at.as_deref());
        out.prop_str("endedAt", self.ended_at.as_deref());
        out.end_object();
    }
}

/// C# `GraphBlockSize(Width, Height)`, a dragged card size.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct GraphBlockSize {
    pub width: f64,
    pub height: f64,
}

/// `MightyGraphBlockSize`'s drag bounds.
pub const MINIMUM_WIDTH: f64 = 300.0;
pub const MINIMUM_HEIGHT: f64 = 140.0;
pub const MAXIMUM_WIDTH: f64 = 1_400.0;
pub const MAXIMUM_HEIGHT: f64 = 1_200.0;
/// `MightyGraphLayout.PlanRecordSuffix`, `WorkSuffix`, `PlanRecordHeight(false)`; `MightyGraphCamera.PendingNodeID`.
pub const PLAN_RECORD_SUFFIX: &str = "plan-record:";
pub const WORK_SUFFIX: &str = "execution";
pub const PLAN_RECORD_FOLDED_HEIGHT: f64 = 104.0;
pub const PENDING_NODE_ID: &str = "pending-input";

impl GraphBlockSize {
    fn normalized_at_least(&self, minimum_height: f64) -> Option<GraphBlockSize> {
        (self.width.is_finite() && self.height.is_finite()).then(|| GraphBlockSize {
            width: MAXIMUM_WIDTH.min(MINIMUM_WIDTH.max(self.width)),
            height: MAXIMUM_HEIGHT.min(minimum_height.max(self.height)),
        })
    }

    /// `Normalized`.
    pub fn normalized(&self) -> Option<GraphBlockSize> {
        self.normalized_at_least(MINIMUM_HEIGHT)
    }

    /// `NormalizedFor(nodeId)`: an answered plan's block may stay as short as it folds.
    pub fn normalized_for(&self, node_id: &str) -> Option<GraphBlockSize> {
        let minimum = if node_suffix(node_id).is_some_and(|s| s.starts_with(PLAN_RECORD_SUFFIX)) {
            PLAN_RECORD_FOLDED_HEIGHT
        } else {
            MINIMUM_HEIGHT
        };
        self.normalized_at_least(minimum)
    }
}

impl Wire for GraphBlockSize {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a block size")?;
        Ok(GraphBlockSize { width: json::float64(o.get("width"), 0.0)?, height: json::float64(o.get("height"), 0.0)? })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_float("width", self.width);
        out.prop_float("height", self.height);
        out.end_object();
    }
}

/// `MightyGraphBlockSize.NodeId`: `<UTF-8 length of the run id>:<run id>:<suffix>`.
pub fn node_id(run_id: &str, suffix: &str) -> String {
    format!("{}:{run_id}:{suffix}", run_id.len())
}

/// `MightyGraphBlockSize.Suffix`: what follows the length-prefixed run id.
pub fn node_suffix(node_id: &str) -> Option<&str> {
    let colon = node_id.find(':')?;
    if colon == 0 {
        return None;
    }
    let count: usize = node_id[..colon].parse().ok().filter(|_| node_id[..colon].bytes().all(|b| b.is_ascii_digit()))?;
    let separator = colon + 1 + count;
    let bytes = node_id.as_bytes();
    if separator >= bytes.len() || bytes[separator] != b':' {
        return None;
    }
    // C# decodes the bytes after it (a cut inside a character reads as U+FFFD there; here it is no suffix).
    node_id.get(separator + 1..)
}
