//! The advisor in a Claude run's graph (ExecutionGraphTracker.Advisor.cs): a `server_tool_use` block named `advisor` in the caller's own
//! message opens a consultation and its `advisor_tool_result` settles it; a turn's result (`usage.iterations[]` of type `advisor_message`)
//! and a transcript line's `advisorModel` name the advisor's model and its tokens. All of it is the run main block's advisor record, never
//! a tool block or a tool row.

use mighty_core::graph::{GraphAdvisor, GraphAdvisorCall, GraphTokenUsage};
use mighty_core::wire;
use serde_json::Value;

use super::{ExecutionGraphTracker, key, prop, text};
use crate::node::{self, ExecutionGraphNode};

/// `FileTools`: the tools that change a file, for `GraphAdvisorCall.BeforeChange`. Bash is not one.
const FILE_TOOLS: [&str; 8] = ["Edit", "Write", "MultiEdit", "NotebookEdit", "apply_patch", "write_file", "replace", "edit_file"];
const MAXIMUM_TOTAL: i32 = 1_000_000;

fn calls(advisor: &GraphAdvisor) -> Vec<GraphAdvisorCall> {
    advisor.calls.iter().flatten().flatten().cloned().collect()
}

/// `GraphAdvisor.Count`: every consultation seen, kept or not.
fn count(advisor: &GraphAdvisor) -> i32 {
    advisor.total.unwrap_or(0).max(calls(advisor).len() as i32)
}

fn empty_advisor() -> GraphAdvisor {
    GraphAdvisor { model: None, usage: None, total: None, calls: Some(vec![]) }
}

fn with_calls(advisor: GraphAdvisor, calls: Vec<GraphAdvisorCall>) -> GraphAdvisor {
    GraphAdvisor { calls: Some(calls.into_iter().map(Some).collect()), ..advisor }
}

/// `AdvisorMessages(usage)`: the advisor's own messages among a usage's iterations — their tokens and the model the last names.
fn advisor_messages(usage: Option<&Value>) -> (GraphTokenUsage, Option<String>) {
    let mut tokens = GraphTokenUsage::default();
    let mut model = None;
    let Some(iterations) = prop(usage, "iterations").and_then(Value::as_array) else { return (tokens, model) };
    for iteration in iterations.iter().take(4_096) {
        if !iteration.is_object() || text(Some(iteration), "type") != Some("advisor_message") {
            continue;
        }
        if let Some(counted) = node::parse_usage(Some(iteration)) {
            tokens = node::add(tokens, counted);
        }
        if let Some(name) = text(Some(iteration), "model").filter(|n| wire::model(n)) {
            model = Some(name.to_string());
        }
    }
    (tokens, model)
}

impl ExecutionGraphTracker {
    /// `AdvisorOwner(root)`: a notice turn's own run for what its main session asks, else the request's.
    fn advisor_owner(&self, root: bool) -> String {
        if !root {
            return self.main_id.clone();
        }
        let Some(run) = self.notice_turn.clone().or_else(|| self.merge_turn.clone()) else { return self.main_id.clone() };
        let id = node::main_node_id(&run);
        if self.kind_of(&id) == Some("main") { id } else { self.main_id.clone() }
    }

    /// `AdvisorAsked`: `caller` consulted the advisor.
    pub(super) fn advisor_asked(&mut self, call: &str, caller: &str, root: bool) {
        if self.provider != "claude" || !wire::identifier(call) || self.advisor_owners.contains_key(call) {
            return;
        }
        let owner = self.advisor_owner(root);
        self.advisor_owners.insert(call.into(), owner.clone());
        self.advisor_order.push(call.into());
        if self.advisor_order.len() > 1_024 {
            let oldest = self.advisor_order.remove(0);
            self.advisor_owners.remove(&oldest);
        }
        let asker = (!root).then(|| caller.to_string());
        let time = self.now();
        let unchanged = !self.changed_files.contains(caller);
        let call = call.to_string();
        self.update(&owner, |node| {
            let advisor = node.advisor.clone().unwrap_or_else(empty_advisor);
            // Asking again is going on with the work: this caller's earlier consultations were not its last step.
            let mut list: Vec<GraphAdvisorCall> = calls(&advisor)
                .into_iter()
                .map(|c| if c.caller == asker { GraphAdvisorCall { followed: Some(true), ..c } } else { c })
                .collect();
            let total = MAXIMUM_TOTAL.min(count(&advisor) + 1);
            list.push(GraphAdvisorCall {
                id: Some(call),
                caller: asker,
                started_at: Some(time),
                ended_at: None,
                state: Some("consulting".into()),
                redacted: None,
                text: None,
                error_code: None,
                before_change: unchanged.then_some(true),
                followed: None,
            });
            let advisor = with_calls(GraphAdvisor { total: Some(total), ..advisor }, list);
            ExecutionGraphNode { advisor: Some(advisor), ..node }
        });
    }

    /// `AdvisorAnswered`: a consultation's result — encrypted, the advice as text, or an error with its code.
    pub(super) fn advisor_answered(&mut self, call: &str, content: Option<&Value>) {
        let Some(owner) = self.advisor_owners.get(call).cloned() else { return };
        let result = content.filter(|c| c.is_object());
        let kind = result.and_then(|r| text(Some(r), "type")).unwrap_or("").to_string();
        let time = self.now();
        self.update(&owner, |node| {
            let Some(advisor) = node.advisor.clone() else { return node };
            let mut list = calls(&advisor);
            let Some(index) = list.iter().position(|c| c.id.as_deref() == Some(call)) else { return node };
            if list[index].state.as_deref() != Some("consulting") {
                return node;
            }
            let mut value = GraphAdvisorCall { ended_at: Some(time), ..list[index].clone() };
            if let Some(code) = text(result, "error_code") {
                value.state = Some("error".into());
                value.error_code = Some(code.into());
            } else if kind.contains("error") {
                value.state = Some("error".into());
            } else {
                value.state = Some(if text(result, "stop_reason") == Some("refusal") { "declined" } else { "done" }.into());
                if kind == "advisor_redacted_result" {
                    value.redacted = Some(true);
                }
                // Only the text the CLI gives: an encrypted reply has none.
                if kind == "advisor_result"
                    && let Some(advice) = text(result, "text")
                {
                    value.text = Some(advice.into());
                }
            }
            list[index] = value;
            ExecutionGraphNode { advisor: Some(with_calls(advisor, list)), ..node }
        });
    }

    /// `AdvisorWorked`: `caller` called a tool — its consultations so far were not its last step; a file-changing tool is remembered.
    pub(super) fn advisor_worked(&mut self, caller: &str, root: bool, tool: Option<&str>) {
        if self.provider != "claude" {
            return;
        }
        if let Some(tool) = tool
            && FILE_TOOLS.contains(&tool)
        {
            self.changed_files.insert(caller.into());
        }
        let owner = self.advisor_owner(root);
        let asker = (!root).then(|| caller.to_string());
        let open = self
            .node(&owner)
            .and_then(|n| n.advisor.as_ref())
            .is_some_and(|a| calls(a).iter().any(|c| c.caller == asker && c.followed != Some(true)));
        if !open {
            return;
        }
        self.update(&owner, |node| {
            let Some(advisor) = node.advisor.clone() else { return node };
            let list = calls(&advisor)
                .into_iter()
                .map(|c| if c.caller == asker { GraphAdvisorCall { followed: Some(true), ..c } } else { c })
                .collect();
            ExecutionGraphNode { advisor: Some(with_calls(advisor, list)), ..node }
        });
    }

    /// `AdvisorNoted`: what a message itself says of the advisor (a transcript's `advisorModel` and the message's final usage).
    pub(super) fn advisor_noted(&mut self, frame: &Value, message: &Value, root: bool) {
        if self.provider != "claude" {
            return;
        }
        let (tokens, said_model) = advisor_messages(prop(Some(message), "usage"));
        let model = said_model.or_else(|| text(Some(frame), "advisorModel").filter(|m| wire::model(m)).map(str::to_string));
        if model.is_none() && node::is_empty(&tokens) {
            return;
        }
        let owner = self.advisor_owner(root);
        if !node::is_empty(&tokens)
            && let Some(id) = key(prop(Some(message), "id"))
        {
            // A message comes as several lines that all carry its usage: counted once, at its latest figure.
            let key = format!("{owner}|{id}");
            let previous = self.advisor_message_usage.get(&key).copied();
            if previous.is_none() {
                self.advisor_message_order.push(key.clone());
                if self.advisor_message_order.len() > 1_024 {
                    let oldest = self.advisor_message_order.remove(0);
                    self.advisor_message_usage.remove(&oldest);
                }
            }
            if previous != Some(tokens) {
                self.advisor_message_usage.insert(key, tokens);
                let sum = self.advisor_message_sum.get(&owner).copied().unwrap_or_default();
                self.advisor_message_sum.insert(owner.clone(), node::add(node::subtract(sum, previous.unwrap_or_default()), tokens));
            }
        }
        self.publish_advisor(&owner, model, None);
    }

    /// `PublishAdvisor`: the advisor's model and tokens on a run main block — the larger of what its messages and its turns' results said.
    fn publish_advisor(&mut self, owner: &str, model: Option<String>, settle: Option<&dyn Fn(GraphAdvisor) -> GraphAdvisor>) {
        let usage = [self.advisor_message_sum.get(owner).copied(), self.advisor_result_sum.get(owner).copied()].into_iter().flatten().fold(
            None::<GraphTokenUsage>,
            |best, next| match best {
                Some(b) if next.total() <= b.total() => Some(b),
                _ => Some(next),
            },
        );
        let Some(current) = self.node(owner).filter(|n| n.kind == "main") else { return };
        let known = current.advisor.as_ref();
        if settle.is_none()
            && !(model.is_some() && known.and_then(|k| k.model.as_ref()) != model.as_ref())
            && !(usage.is_some() && known.and_then(|k| k.usage) != usage)
        {
            return;
        }
        self.update(owner, |node| {
            let mut advisor = node.advisor.clone().unwrap_or_else(empty_advisor);
            if model.is_some() {
                advisor.model = model;
            }
            if usage.is_some() {
                advisor.usage = usage;
            }
            if let Some(settle) = settle {
                advisor = settle(advisor);
            }
            ExecutionGraphNode { advisor: Some(advisor), ..node }
        });
    }

    /// `AdvisorTurnEnded`: a root turn's result — what it says the advisor's own messages cost, and the end of what the main session
    /// still had out.
    pub(super) fn advisor_turn_ended(&mut self, frame: &Value) {
        if self.provider != "claude" {
            return;
        }
        let owner = self.advisor_owner(true);
        let (tokens, model) = advisor_messages(prop(Some(frame), "usage"));
        if !node::is_empty(&tokens) {
            let sum = self.advisor_result_sum.get(&owner).copied().unwrap_or_default();
            self.advisor_result_sum.insert(owner.clone(), node::add(sum, tokens));
        }
        let outstanding = self
            .node(&owner)
            .and_then(|n| n.advisor.as_ref())
            .is_some_and(|a| calls(a).iter().any(|c| c.caller.is_none() && c.state.as_deref() == Some("consulting")));
        if node::is_empty(&tokens) && model.is_none() && !outstanding {
            return;
        }
        let time = self.now();
        let stop = move |advisor: GraphAdvisor| {
            let list = calls(&advisor)
                .into_iter()
                .map(|c| {
                    if c.caller.is_none() && c.state.as_deref() == Some("consulting") {
                        GraphAdvisorCall { state: Some("stopped".into()), ended_at: Some(time.clone()), ..c }
                    } else {
                        c
                    }
                })
                .collect();
            with_calls(advisor, list)
        };
        self.publish_advisor(&owner, model, if outstanding { Some(&stop) } else { None });
    }

    /// `StopAdvisorCalls`: the process ended; a consultation still out never got its result.
    pub(super) fn stop_advisor_calls(&mut self) {
        let time = self.now();
        for id in self.order.clone() {
            let open = self
                .node(&id)
                .and_then(|n| n.advisor.as_ref())
                .is_some_and(|a| calls(a).iter().any(|c| c.state.as_deref() == Some("consulting")));
            if !open {
                continue;
            }
            let time = time.clone();
            self.update(&id, |node| {
                let Some(advisor) = node.advisor.clone() else { return node };
                let list = calls(&advisor)
                    .into_iter()
                    .map(|c| {
                        if c.state.as_deref() == Some("consulting") {
                            GraphAdvisorCall { state: Some("stopped".into()), ended_at: Some(time.clone()), ..c }
                        } else {
                            c
                        }
                    })
                    .collect();
                ExecutionGraphNode { advisor: Some(with_calls(advisor, list)), ..node }
            });
        }
    }
}
