//! Finished background work and its notice runs (ExecutionGraphTracker.cs:648-713, 1024-1329; C# `BackgroundNoticeRuns`): a task's or a
//! background sub-agent's end settles its block and copies it into a notice run, a root-level notice after the request's own turn opens
//! a turn whose answer is that run's result, and a running agent's message opens (or joins) a 하위 에이전트 메시지 run.

use std::sync::LazyLock;

use regex::Regex;
use serde_json::Value;

use super::{
    ExecutionGraphTracker, HeldLine, MESSAGE_RUN_LIMIT, NOTICE_HEADROOM, NOTICE_LINE_LIMIT, NoticeOpening, is_true, key, prop, text,
};
use crate::handback;
use crate::node::{self, ExecutionGraphNode, MAXIMUM_NODES};
use crate::notice::{self, Opener};
use crate::support;
use mighty_core::wire;

/// A background command's exit code in its notification's summary.
static EXIT_CODE: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"exit code (?<code>-?[0-9]{1,10})").expect("regex"));

/// `Tag(body, name)`: the trimmed text between `<name>` and `</name>`, none when absent or empty.
fn tag(body: &str, name: &str) -> Option<String> {
    let open = format!("<{name}>");
    let start = body.find(&open)? + open.len();
    let end = body[start..].find(&format!("</{name}>"))? + start;
    let value = body[start..end].trim();
    (!value.is_empty()).then(|| value.to_string())
}

impl ExecutionGraphTracker {
    /// The `result` of a notice turn (ExecutionGraphTracker.cs:648): its answer is its own run's result, goes on in an earlier notice run,
    /// or opens a run of its own; it never replaces the request's result.
    pub(super) fn notice_result(&mut self, value: &Value) {
        let frame = Some(value);
        let (run, merged, absorbed) = (self.notice_turn.clone(), self.merge_turn.clone(), self.absorbed_run.clone());
        let lines = self.turn_lines.clone();
        self.clear_notice_turn();
        let answer = text(frame, "result").map(str::trim).unwrap_or("").to_string();
        let failed = is_true(frame, "is_error") || text(frame, "subtype").unwrap_or("").starts_with("error");
        if let Some(run) = run {
            // A notice turn's answer is its own run's result; the request's result stays as it was.
            let answer = answer.clone();
            self.update(&node::main_node_id(&run), |n| ExecutionGraphNode {
                output: if answer.is_empty() { n.output.clone() } else { Some(answer) },
                state: if failed { "error" } else { "completed" }.into(),
                ..n
            });
            return;
        }
        if let Some(merged) = merged {
            // A turn about blocks an earlier notice run already holds goes on in that run's result, as a new paragraph.
            if !answer.is_empty() {
                self.update(&node::main_node_id(&merged), |n| ExecutionGraphNode {
                    output: Some(paragraph(n.output.as_deref(), &answer)),
                    ..n
                });
            }
            self.set_notice_state(&merged, if failed { "error" } else { "completed" });
            return;
        }
        // A notice answer no notice run took never extends the request's result.
        if answer.is_empty() {
            return;
        }
        if let Some(absorbed) = absorbed {
            // The lines this turn held are about the same work: they join the run's request.
            self.add_opening(&absorbed, &lines);
            let answer = answer.clone();
            self.update(&node::main_node_id(&absorbed), |n| ExecutionGraphNode { output: Some(answer), ..n });
            if failed {
                self.set_notice_state(&absorbed, "error");
            }
            return;
        }
        let peer = prop(frame, "origin").filter(|o| o.is_object()).is_some_and(|o| text(Some(o), "kind") == Some("peer"));
        let opener = if peer { Opener::Message } else { Opener::Work };
        // Two pieces of work that end close together: the second turn brings no notice of its own, only its result naming its
        // notification's task run (origin.runId); that answer goes on in the run that already holds the work.
        if !peer
            && lines.is_empty()
            && let Some(earlier) = self.held_run(value)
        {
            self.answer_again(&earlier, &lines, &answer, failed);
            return;
        }
        if opener == Opener::Message
            && let Some(again) = self.message_run(&lines)
        {
            self.answer_again(&again, &lines, &answer, failed);
            return;
        }
        if self.open_own(opener, &lines, &answer, failed, true) {
            return;
        }
        // No room for a run of its own: the answer goes on in the latest notice run. It is never dropped.
        if let Some(latest) = self.latest_notice_run() {
            self.answer_again(&latest, &lines, &answer, failed);
            return;
        }
        if opener == Opener::Message {
            // A message's answer may take the nodes kept for work copies; at the very cap it joins the log of the one agent that sent the
            // messages, which adds no node.
            if self.open_own(opener, &lines, &answer, failed, false) {
                return;
            }
            if let Some(sender) = sole_agent(&lines)
                && self.nodes.contains_key(&sender)
            {
                let entry = self.entry(
                    node::identifier(&self.run_id, &format!("{sender}:reply:{answer}")),
                    "assistant",
                    format!("**{}**\n\n{answer}", self.locale.get("graph.background.reply")),
                    None,
                );
                self.append(entry, &sender);
                return;
            }
        }
        // The last resort, at the very cap with nowhere else: the answer follows in the request's result under its kind.
        let header = self.locale.get_with("graph.result.afterSteer", &[("block", &notice::kind(opener, &self.locale))]);
        let main = self.main_id.clone();
        self.update(&main, |n| ExecutionGraphNode {
            output: Some(support::with_notice_section(n.output.as_deref(), &header, &answer)),
            ..n
        });
    }

    /// `OpenOwn`: the answer's own notice run.
    fn open_own(&mut self, opener: Opener, lines: &[HeldLine], answer: &str, failed: bool, reserving: bool) -> bool {
        let (names, senders) = senders(lines);
        let line_texts: Vec<String> = lines.iter().filter_map(|h| h.line.clone()).collect();
        let Some(own) = self.open_notice_run(false, opener, names, line_texts, senders, sole_agent(lines), reserving) else { return false };
        let answer = answer.to_string();
        self.update(&node::main_node_id(&own), |n| ExecutionGraphNode { output: Some(answer), ..n });
        if failed {
            self.set_notice_state(&own, "error");
        }
        true
    }

    /// `SettleTasks(text, notice)`: each `<task-notification>` settles its block (a background command's exit code kept) and copies it
    /// into a notice run; a notification for work this run never started names the turn's run.
    pub(super) fn settle_tasks(&mut self, text_value: &str, notice: bool) {
        const OPEN: &str = "<task-notification>";
        const CLOSE: &str = "</task-notification>";
        let mut remaining = text_value;
        while let Some(start) = remaining.find(OPEN) {
            let Some(end) = remaining[start + OPEN.len()..].find(CLOSE).map(|at| at + start + OPEN.len()) else { break };
            let body = remaining[start + OPEN.len()..end].to_string();
            remaining = &remaining[end + CLOSE.len()..];
            let by_tool = tag(&body, "tool-use-id").map(|t| node::agent_node_id(&self.run_id, &t)).filter(|n| self.nodes.contains_key(n));
            // The tool use's block while it is open; once it ended, the task id's.
            let aliased = tag(&body, "task-id").and_then(|t| self.task_aliases.get(&t).cloned());
            let id = match &by_tool {
                Some(launched) if !node::terminal(&self.nodes[launched].state) => Some(launched.clone()),
                _ => aliased.or(by_tool),
            };
            let kind = id.as_ref().and_then(|i| self.kind_of(i)).map(str::to_string);
            let (Some(id), Some(kind)) = (id, kind.filter(|k| matches!(k.as_str(), "task" | "agent"))) else {
                // Work this run never started (an earlier process's) has no block to copy: its line names the turn's run.
                if notice {
                    let line = tag(&body, "summary").or_else(|| tag(&body, "status"));
                    self.join_turn(Opener::Work, None, line.as_deref(), None);
                }
                continue;
            };
            if let Some(task_run) = tag(&body, "run-id")
                && (self.task_runs.contains_key(&task_run) || self.task_runs.len() < 512)
            {
                self.task_runs.insert(task_run, id.clone());
            }
            // A foreground sub-agent's end is reported as a background task's, right before the Agent call's own result: no finished
            // background work here, so the notification settles nothing, copies nothing and opens no notice run.
            if kind == "agent"
                && let Some(foreground) = tag(&body, "tool-use-id")
                && id == node::agent_node_id(&self.run_id, &foreground)
                && !self.background_tools.contains(&foreground)
            {
                continue;
            }
            let status = tag(&body, "status").map(|s| s.to_lowercase()).unwrap_or_else(|| "completed".into());
            let state = match status.as_str() {
                "completed" => "completed",
                "failed" | "error" => "error",
                _ => "stopped",
            };
            let summary = tag(&body, "summary");
            if kind == "agent" {
                // An agent's result is its report; the summary only fills an empty one until the report comes.
                let empty = self.nodes[&id].output.as_deref().is_none_or(str::is_empty);
                let fill = summary.clone().filter(|_| empty);
                self.update(&id, |n| ExecutionGraphNode {
                    state: if node::terminal(&n.state) { n.state.clone() } else { state.into() },
                    output: fill.or(n.output.clone()),
                    ..n
                });
                if empty
                    && summary.is_some()
                    && let Some(output) = self.nodes[&id].output.clone()
                {
                    self.agent_summaries.insert(id.clone(), output);
                }
                self.add_notice(&id, notice);
            } else {
                let file = tag(&body, "output-file");
                // "Background command "…" completed (exit code 0)", "… failed with exit code 3".
                let code = summary.as_deref().and_then(|s| EXIT_CODE.captures(s)).and_then(|c| c["code"].parse::<i32>().ok());
                let claude = self.provider == "claude";
                self.update(&id, |mut n| {
                    if let Some(file) = file {
                        n.output_file = Some(file);
                    }
                    if node::terminal(&n.state) {
                        return n;
                    }
                    n.state = state.into();
                    n.output = summary.or(n.output);
                    if claude && let Some(code) = code {
                        let mut figures = n.figures.take().unwrap_or_default();
                        figures.exit_code = Some(code);
                        n.figures = Some(figures);
                    }
                    n
                });
                self.add_notice(&id, notice);
            }
        }
    }

    /// `AddNotice(id, root)`: a finished background block goes into a notice run as a copy.
    fn add_notice(&mut self, id: &str, root: bool) {
        if self.provider != "claude" || !self.nodes.contains_key(id) {
            return;
        }
        let opens_turn = root && self.turn_ended && self.steered_turn.is_none();
        if let Some(earlier) = self.copied_in.get(id).cloned() {
            self.copy(id, &earlier);
            if self.notice_turn.is_none() && self.merge_turn.is_none() && opens_turn {
                self.merge_turn = Some(earlier.clone());
                self.merge_restore = self.nodes.get(&node::main_node_id(&earlier)).map(|n| n.state.clone());
                self.set_notice_state(&earlier, "running");
            }
            return;
        }
        let run = if let Some(open) = self.notice_turn.clone() {
            open
        } else if opens_turn || self.merge_turn.is_some() {
            let Some(opened) = self.open_notice_run(true, Opener::Work, vec![], vec![], vec![], None, true) else { return };
            // The turn brought new work after all: the earlier run gets nothing of it and ends as it was.
            if let Some(merged) = self.merge_turn.take() {
                let restore = self.merge_restore.take().unwrap_or_else(|| "completed".into());
                self.set_notice_state(&merged, &restore);
            }
            self.notice_turn = Some(opened.clone());
            opened
        } else if let Some(absorbed) = self.absorbed_run.clone() {
            absorbed
        } else {
            let Some(opened) = self.open_notice_run(false, Opener::Work, vec![], vec![], vec![], None, true) else { return };
            self.absorbed_run = Some(opened.clone());
            opened
        };
        self.copy(id, &run);
    }

    /// `Copy(id, run)`: creates or updates block `id`'s copy in notice run `run`, and the run's request lines.
    fn copy(&mut self, id: &str, run: &str) {
        let Some(block) = self.nodes.get(id).cloned() else { return };
        let main = node::main_node_id(run);
        let joined = format!("{run}:{id}");
        let copy_id = if wire::identifier(&joined) { joined } else { node::identifier(run, &format!("copy:{id}")) };
        if !self.nodes.contains_key(&copy_id) {
            if self.nodes.len() >= MAXIMUM_NODES {
                return;
            }
            let mut copy = ExecutionGraphNode::new(copy_id.clone(), run.into(), Some(main), &block.kind, &block.state, block.title.clone());
            copy.input = block.input.clone();
            copy.output = block.output.clone();
            copy.background = Some(true);
            copy.output_file = block.output_file.clone();
            copy.source_block_id = Some(id.into());
            copy.identity = block.identity.clone();
            copy.figures = block.figures.clone();
            let Some(normalized) = node::normalized(&copy, false) else { return };
            self.insert(normalized);
            self.notice_copies.entry(run.into()).or_default().push(copy_id);
            if self.copied_in.contains_key(id) || self.copied_in.len() < 512 {
                self.copied_in.insert(id.into(), run.into());
            }
            // A run holding a copy is named after the work, no longer an agent's messages alone.
            self.message_agents.remove(run);
        } else {
            self.update(&copy_id, |n| ExecutionGraphNode {
                title: block.title.clone(),
                input: block.input.clone(),
                state: block.state.clone(),
                output: block.output.clone(),
                output_file: block.output_file.clone(),
                identity: block.identity.clone(),
                figures: block.figures.clone(),
                ..n
            });
        }
        self.describe(run);
    }

    /// `Describe(run)`: a notice run's title and request lines — its opener's lines, then each copied block and how it ended.
    fn describe(&mut self, run: &str) {
        let copies: Vec<ExecutionGraphNode> =
            self.notice_copies.get(run).map(|c| c.iter().filter_map(|id| self.nodes.get(id).cloned()).collect()).unwrap_or_default();
        let opened = self.notice_openers.get(run).cloned().unwrap_or_else(NoticeOpening::empty);
        let name =
            |copy: &ExecutionGraphNode| if copy.title.is_empty() { copy.input.clone().unwrap_or_default() } else { copy.title.clone() };
        let mut lines = opened.lines.clone();
        for copy in &copies {
            let word = self.locale.get(match copy.state.as_str() {
                "completed" => "graph.block.taskNotice.completed",
                "error" => "graph.block.taskNotice.failed",
                _ => "graph.block.taskNotice.stopped",
            });
            lines.push(format!("{} · {word}", name(copy)));
        }
        let title = if copies.is_empty() {
            notice::title(&notice::kind(opened.opener, &self.locale), &opened.names, &self.locale)
        } else {
            let names: Vec<String> = copies.iter().map(name).collect();
            notice::title(&notice::kind(Opener::Work, &self.locale), &names, &self.locale)
        };
        let input = lines.join("\n");
        self.update(&node::main_node_id(run), |n| ExecutionGraphNode { title, input: Some(input), ..n });
    }

    /// `JoinTurn(opener, name, line, agent)`: a root message about no block a notice run copies. It joins the turn's open notice run, or
    /// opens that turn's run when it opens a turn; otherwise its line waits for the turn's answer.
    pub(super) fn join_turn(&mut self, opener: Opener, name: Option<&str>, line: Option<&str>, agent: Option<&str>) {
        if self.provider != "claude" {
            return;
        }
        let held = HeldLine {
            line: line.map(str::trim).filter(|l| !l.is_empty()).map(Into::into),
            name: name.map(Into::into),
            agent: agent.map(Into::into),
        };
        if held.name.is_none() && held.line.is_none() {
            return;
        }
        if let Some(run) = self.notice_turn.clone().or_else(|| self.merge_turn.clone()) {
            self.add_opening(&run, &[held]);
        } else if self.turn_ended && self.steered_turn.is_none() {
            let mut lines = std::mem::take(&mut self.turn_lines);
            lines.push(held);
            lines.truncate(NOTICE_LINE_LIMIT);
            if opener == Opener::Message
                && let Some(again) = self.message_run(&lines)
            {
                // The turn answers in that run again, as a turn about blocks it copied does (mergeTurn).
                self.merge_turn = Some(again.clone());
                self.merge_restore = self.nodes.get(&node::main_node_id(&again)).map(|n| n.state.clone());
                self.set_notice_state(&again, "running");
                self.add_opening(&again, &lines);
            } else {
                let (names, senders) = senders(&lines);
                let line_texts: Vec<String> = lines.iter().filter_map(|h| h.line.clone()).collect();
                match self.open_notice_run(true, opener, names, line_texts, senders, sole_agent(&lines), true) {
                    Some(opened) => self.notice_turn = Some(opened),
                    None => self.turn_lines.extend(lines),
                }
            }
        } else if self.turn_lines.len() < NOTICE_LINE_LIMIT {
            self.turn_lines.push(held);
        }
    }

    /// `MessageRun(lines)`: the 하위 에이전트 메시지 run a turn of these messages answers in again: the latest notice run while it holds
    /// only messages of the one agent that sent all of them, or past `MESSAGE_RUN_LIMIT` the latest message run.
    fn message_run(&self, lines: &[HeldLine]) -> Option<String> {
        let latest = self.latest_message_run.clone().filter(|l| self.nodes.contains_key(&node::main_node_id(l)))?;
        if let Some(agent) = sole_agent(lines)
            && self.message_agents.get(&latest) == Some(&agent)
            && self.latest_notice_run().as_ref() == Some(&latest)
        {
            return Some(latest);
        }
        (self.message_run_count >= MESSAGE_RUN_LIMIT).then_some(latest)
    }

    /// `HeldRun(result)`: the notice run that already holds the work a notification turn's result answers (its `origin.runId`).
    fn held_run(&self, result: &Value) -> Option<String> {
        let origin = prop(Some(result), "origin").filter(|o| o.is_object())?;
        let task = key(prop(Some(origin), "runId"))?;
        let block = self.task_runs.get(task)?;
        let run = self.copied_in.get(block)?;
        self.nodes.contains_key(&node::main_node_id(run)).then(|| run.clone())
    }

    /// `LatestNoticeRun`: the latest notice run opened, if any.
    fn latest_notice_run(&self) -> Option<String> {
        if self.notice_index == 0 {
            return None;
        }
        let run = notice::run_id(&self.run_id, self.notice_index);
        self.nodes.contains_key(&node::main_node_id(&run)).then_some(run)
    }

    /// `AddOpening(run, lines)`: lines and names that join a notice run's request and title, each sender named once.
    fn add_opening(&mut self, run: &str, lines: &[HeldLine]) {
        if lines.is_empty() {
            return;
        }
        let mut opened = self.notice_openers.get(run).cloned().unwrap_or_else(NoticeOpening::empty);
        for held in lines {
            if let (Some(name), Some(key)) = (&held.name, sender_key(held))
                && !opened.senders.contains(&key)
                && opened.names.len() < NOTICE_LINE_LIMIT
            {
                opened.names.push(name.clone());
                opened.senders.push(key);
            }
            if let Some(text) = &held.line
                && opened.lines.len() < NOTICE_LINE_LIMIT
            {
                opened.lines.push(text.clone());
            }
            if held.agent.is_none() || self.message_agents.get(run) != held.agent.as_ref() {
                self.message_agents.remove(run);
            }
        }
        self.notice_openers.insert(run.into(), opened);
        self.describe(run);
    }

    /// `AnswerAgain(run, lines, answer, failed)`: a notice answer that goes on in an earlier notice run.
    fn answer_again(&mut self, run: &str, lines: &[HeldLine], answer: &str, failed: bool) {
        self.add_opening(run, lines);
        let answer = answer.to_string();
        self.update(&node::main_node_id(run), |n| ExecutionGraphNode { output: Some(paragraph(n.output.as_deref(), &answer)), ..n });
        self.set_notice_state(run, if failed { "error" } else { "completed" });
    }

    /// `SetNoticeState(run, state)`: a notice run's main block running again for a turn that answers in it, or ended. Unlike `update`, an
    /// ended main may run again.
    fn set_notice_state(&mut self, run: &str, state: &str) {
        let main = node::main_node_id(run);
        if self.finished {
            return;
        }
        let Some(current) = self.nodes.get(&main) else { return };
        if current.state == state {
            return;
        }
        let Some(mut normalized) = node::normalized(&ExecutionGraphNode { state: state.into(), ..current.clone() }, false) else { return };
        normalized.updated_at = mighty_core::time::now_o();
        self.nodes.insert(main, normalized.clone());
        self.send(&normalized);
        if state == "running" {
            self.notice_revision += 1;
        }
    }

    /// `OpenNoticeRun`: the next notice run's main block, running until a notice turn's result or complete at once for absorbed notices.
    /// A message run leaves `NOTICE_HEADROOM` nodes free unless `reserving` is false.
    #[allow(clippy::too_many_arguments)]
    fn open_notice_run(
        &mut self,
        running: bool,
        opener: Opener,
        names: Vec<String>,
        lines: Vec<String>,
        senders: Vec<String>,
        agent: Option<String>,
        reserving: bool,
    ) -> Option<String> {
        let room = if opener == Opener::Message && reserving { MAXIMUM_NODES - NOTICE_HEADROOM } else { MAXIMUM_NODES };
        if self.nodes.len() >= room {
            return None;
        }
        let run = notice::run_id(&self.run_id, self.notice_index + 1);
        let mut main = ExecutionGraphNode::new(
            node::main_node_id(&run),
            run.clone(),
            None,
            "main",
            if running { "running" } else { "completed" },
            notice::title(&notice::kind(opener, &self.locale), &names, &self.locale),
        );
        main.input = (!lines.is_empty()).then(|| lines.join("\n"));
        let normalized = node::normalized(&main, false)?;
        self.notice_index += 1;
        self.notice_revision += 1;
        self.notice_openers.insert(run.clone(), NoticeOpening { opener, names, lines, senders });
        if opener == Opener::Message {
            self.message_run_count += 1;
            self.latest_message_run = Some(run.clone());
            if let Some(agent) = agent {
                self.message_agents.insert(run.clone(), agent);
            }
        }
        self.insert(normalized);
        self.notice_runs.push(run.clone());
        Some(run)
    }

    /// `ClearNoticeTurn`: a notice turn or a steered turn ended; the next one starts with no notices.
    pub(super) fn clear_notice_turn(&mut self) {
        self.steered_turn = None;
        self.notice_turn = None;
        self.absorbed_run = None;
        self.merge_turn = None;
        self.merge_restore = None;
        self.turn_lines.clear();
    }

    /// `SettleHandbacks(text, origin, notice)`: messages a background sub-agent sent. Its framed final report is its result and ends it,
    /// copying the block into a notice run; a message it sent while still running joins its block's log.
    pub(super) fn settle_handbacks(&mut self, text_value: &str, origin: Option<&Value>, notice: bool) {
        let messages = handback::parse(text_value);
        let lone = messages.len() == 1;
        let sender = if lone { key(prop(origin, "senderTaskId")).map(str::to_string) } else { None };
        for message in messages {
            let id = self
                .task_aliases
                .get(&message.from)
                .or_else(|| sender.as_ref().and_then(|s| self.task_aliases.get(s)))
                .or_else(|| self.agent_names.get(&message.from))
                .cloned();
            let node = id.as_ref().and_then(|i| self.nodes.get(i)).filter(|n| n.kind == "agent").cloned();
            let (Some(id), Some(node)) = (id, node) else {
                // A sender this run does not know: its message is the turn's, in a 하위 에이전트 메시지 run without a name.
                if notice && !message.body.is_empty() {
                    self.join_turn(Opener::Message, None, Some(&message.body), None);
                }
                continue;
            };
            if !message.is_handback && !(lone && is_true(origin, "handback")) {
                if !message.body.is_empty() {
                    let entry = self.entry(
                        node::identifier(&self.run_id, &format!("{id}:peer:{}", message.body)),
                        "assistant",
                        message.body.clone(),
                        None,
                    );
                    self.append(entry, &id);
                    // A running agent's message opens (or joins) the turn's 하위 에이전트 메시지 run, named after the agent.
                    if notice {
                        let title = (!node.title.is_empty()).then_some(node.title.as_str());
                        self.join_turn(Opener::Message, title, Some(&message.body), Some(&id));
                    }
                }
                continue;
            }
            let terminal = node::terminal(&node.state);
            // A block that already ended keeps its state; its result takes the report only when empty or the notification's summary.
            let replaceable =
                !terminal || node.output.as_deref().is_none_or(str::is_empty) || node.output.as_ref() == self.agent_summaries.get(&id);
            let report = replaceable && !message.body.is_empty();
            let body = message.body.clone();
            self.update(&id, |n| ExecutionGraphNode {
                state: if terminal { n.state.clone() } else { "completed".into() },
                output: if report { Some(body) } else { n.output.clone() },
                ..n
            });
            if report {
                self.agent_summaries.remove(&id);
            }
            self.add_notice(&id, notice);
        }
    }
}

/// The result with `answer` after it as a new paragraph.
fn paragraph(output: Option<&str>, answer: &str) -> String {
    match output {
        Some(o) if !o.is_empty() => format!("{o}\n\n{answer}"),
        _ => answer.to_string(),
    }
}

/// `SoleAgent(lines)`: the one agent block that sent every one of these lines.
fn sole_agent(lines: &[HeldLine]) -> Option<String> {
    let agent = lines.first()?.agent.clone()?;
    lines.iter().all(|h| h.agent.as_deref() == Some(agent.as_str())).then_some(agent)
}

/// `SenderKey`: who a line's name stands for, its agent block or the name itself.
fn sender_key(held: &HeldLine) -> Option<String> {
    held.agent.clone().or_else(|| held.name.clone())
}

/// `Senders(lines)`: the names these lines give a run and who each stands for, each sender once, at most `NOTICE_LINE_LIMIT`.
fn senders(lines: &[HeldLine]) -> (Vec<String>, Vec<String>) {
    let mut names = vec![];
    let mut senders: Vec<String> = vec![];
    for held in lines {
        let (Some(name), Some(key)) = (&held.name, sender_key(held)) else { continue };
        if senders.contains(&key) || names.len() >= NOTICE_LINE_LIMIT {
            continue;
        }
        names.push(name.clone());
        senders.push(key);
    }
    (names, senders)
}
