//! What the page is sent of the state: C# `WebBridge.SendSnapshot`/`Flush` (MightyCode.WinUI/WebBridge.cs:110-186) and `WebDelta`
//! (WebDelta.cs), rule for rule, with the same JSON text. The page applies it with `store/delta.ts` (`applySessionDelta`,
//! `mergeSessions`); the tests below hold the two together (a port of delta.ts applies random changes' deltas and must reach the new
//! snapshot) and hold this to C# (`tools/parity`'s `delta` command over the same sequence, byte for byte).
//!
//! - The snapshot `{kind:"snapshot", snapshot}`: the whole state, the graph agents' output files left on this side (`PageSnapshot`).
//! - A delta `{kind:"delta", sessions, removed, order?, state?}`: each pane whose record is not the one sent last (an `Arc` other
//!   than the sent one) as `{id, session}` when the page has not had it, else as its change `{id, set?, unset?, logs?, graphRuns?}`
//!   — the fields whose JSON differs, the ones now absent, and the two long lists by id (`{drop?, replaced?, added?, order?}`); a
//!   pane made anew with the same values is no change. `removed` always, `order` when the ids or their order changed, `state` (the
//!   state without its panes) when its JSON text changed. Nothing at all when nothing the page holds changed.
//! - Entries compare by value (`SameEntry`), runs and agents by the fields C# compares (`SameRun`, `SameAgent`: a run's advisor,
//!   configured advisor and carried steps are not among them, so a change to those alone is not sent, as in C#).

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use mighty_core::AppSnapshot;
use mighty_core::graph::{MightyGraphAgent, MightyGraphRun};
use mighty_core::json::JsonOut;
use mighty_core::model::{LogEntry, RunSession};
use serde_json::{Map, Value};

/// What the bridge last sent: the state's revision, the panes by id, their fields, the order, the state's text.
#[derive(Default)]
pub struct Bridge {
    sent_revision: Option<u64>,
    sent_sessions: HashMap<String, Arc<RunSession>>,
    sent_fields: HashMap<String, (Arc<RunSession>, Map<String, Value>)>,
    sent_order: Vec<String>,
    sent_state: Option<String>,
}

/// `WebBridge.StateJson`: every field of the state but the panes.
pub fn state_json(state: &AppSnapshot) -> String {
    let mut out = JsonOut::new();
    state.write_with(&mut out, false, false);
    out.finish()
}

/// `WebDelta.PageSnapshot` written: the state as the page is sent it whole.
pub fn page_snapshot(state: &AppSnapshot) -> String {
    let mut out = JsonOut::new();
    state.write_with(&mut out, true, false);
    out.finish()
}

/// `WebDelta.Fields`: a pane's fields but its two lists, as JSON values (read back from the text it writes).
pub fn fields(session: &RunSession) -> Map<String, Value> {
    let mut out = JsonOut::new();
    session.write_with(&mut out, false, false);
    match serde_json::from_str(out.as_str()) {
        Ok(Value::Object(map)) => map,
        _ => Map::new(),
    }
}

/// `WebDelta.SameEntry`: every field an entry writes.
pub fn same_entry(a: &LogEntry, b: &LogEntry) -> bool {
    std::ptr::eq(a, b) || a == b
}

fn same_list<T>(a: Option<&[T]>, b: Option<&[T]>, same: impl Fn(&T, &T) -> bool) -> bool {
    match (a, b) {
        (None, None) => true,
        (Some(x), Some(y)) => x.len() == y.len() && x.iter().zip(y).all(|(p, q)| same(p, q)),
        _ => false,
    }
}

/// `WebDelta.SameAgent`.
fn same_agent(a: &MightyGraphAgent, b: &MightyGraphAgent) -> bool {
    std::ptr::eq(a, b)
        || a.id == b.id
            && a.parent_id == b.parent_id
            && a.title == b.title
            && a.input == b.input
            && a.status == b.status
            && a.kind == b.kind
            && a.usage == b.usage
            && a.activity_generation == b.activity_generation
            && a.background == b.background
            && a.notice_reply == b.notice_reply
            && a.source_block_id == b.source_block_id
            && a.identity == b.identity
            && a.figures == b.figures
            && same_list(a.entries.as_deref(), b.entries.as_deref(), same_entry)
            && same_list(a.response_records.as_deref(), b.response_records.as_deref(), |x, y| x == y)
}

/// `WebDelta.SameRun`: every field `MightyGraphRun` writes that C# compares.
pub fn same_run(a: &MightyGraphRun, b: &MightyGraphRun) -> bool {
    std::ptr::eq(a, b)
        || a.id == b.id
            && a.input == b.input
            && a.status == b.status
            && a.source_run_id == b.source_run_id
            && a.final_output == b.final_output
            && a.provider == b.provider
            && a.node_model_label == b.node_model_label
            && a.configured_model == b.configured_model
            && a.notice_run == b.notice_run
            && a.notice_title == b.notice_title
            && a.usage == b.usage
            && a.configured_effort == b.configured_effort
            && same_list(a.steps.as_deref(), b.steps.as_deref(), |x, y| x == y)
            && same_list(a.root_entries.as_deref(), b.root_entries.as_deref(), same_entry)
            && same_list(a.result_entries.as_deref(), b.result_entries.as_deref(), same_entry)
            && same_list(a.agents.as_deref(), b.agents.as_deref(), same_agent)
            && same_list(a.response_records.as_deref(), b.response_records.as_deref(), |x, y| x == y)
}

/// `WebDelta.ListChange`.
pub struct ListChange<'a, T> {
    pub drop: usize,
    pub replaced: Vec<&'a T>,
    pub added: Vec<&'a T>,
    pub order: Option<Vec<String>>,
}

impl<T> ListChange<'_, T> {
    pub fn empty(&self) -> bool {
        self.drop == 0 && self.replaced.is_empty() && self.added.is_empty() && self.order.is_none()
    }
}

/// `WebDelta.Diff` (delta.ts `diffList`): the usual changes (the last record streamed, records appended, the oldest trimmed) need
/// no order; any other change sends the whole id order.
pub fn diff<'a, T>(previous: &'a [T], next: &'a [T], id: impl Fn(&T) -> &str, same: impl Fn(&T, &T) -> bool) -> ListChange<'a, T> {
    let mut change = ListChange { drop: 0, replaced: vec![], added: vec![], order: None };
    let mut drop = previous.len();
    if let Some(first) = next.first() {
        let first = id(first);
        if let Some(at) = previous.iter().position(|r| id(r) == first) {
            drop = at;
        }
    }
    let kept = previous.len() - drop;
    let mut simple = next.len() >= kept;
    let mut index = 0;
    while simple && index < kept {
        simple = id(&next[index]) == id(&previous[drop + index]);
        index += 1;
    }
    if simple && drop > 0 && next.len() > kept {
        // An added record must be new: one of the dropped ones coming back is a reordering.
        let dropped: HashSet<&str> = previous[..drop].iter().map(&id).collect();
        simple = next[kept..].iter().all(|r| !dropped.contains(id(r)));
    }
    if simple {
        change.drop = drop;
        for index in 0..kept {
            if !same(&previous[drop + index], &next[index]) {
                change.replaced.push(&next[index]);
            }
        }
        change.added.extend(next[kept..].iter());
        return change;
    }
    // C#: old[id(record)] = record — the last of a repeated id.
    let mut old: HashMap<&str, &T> = HashMap::new();
    for record in previous {
        old.insert(id(record), record);
    }
    let mut order = Vec::with_capacity(next.len());
    for record in next {
        let key = id(record);
        order.push(key.to_string());
        match old.get(key) {
            None => change.added.push(record),
            Some(before) if !same(before, record) => change.replaced.push(record),
            Some(_) => {}
        }
    }
    change.order = Some(order);
    change
}

fn write_list<T>(out: &mut JsonOut, name: &str, change: &ListChange<T>, each: impl Fn(&mut JsonOut, &T)) {
    out.name(name);
    out.begin_object();
    if change.drop > 0 {
        out.prop_int("drop", change.drop as i64);
    }
    for (key, items) in [("replaced", &change.replaced), ("added", &change.added)] {
        if !items.is_empty() {
            out.name(key);
            out.begin_array();
            for item in items {
                each(out, item);
            }
            out.end_array();
        }
    }
    if let Some(order) = &change.order {
        out.prop_strings("order", Some(order));
    }
    out.end_object();
}

/// `WebDelta.Write`: `next` as a change from `previous` (whose fields were `before`), or nothing when nothing the page holds changed.
pub fn write(out: &mut JsonOut, previous: &RunSession, before: &Map<String, Value>, next: &RunSession, after: &Map<String, Value>) -> bool {
    let set: Vec<(&String, &Value)> = after.iter().filter(|(key, value)| before.get(*key) != Some(*value)).collect();
    let unset: Vec<String> = before.keys().filter(|key| !after.contains_key(*key)).cloned().collect();
    let logs = diff(previous.logs(), next.logs(), LogEntry::id, same_entry);
    let runs = diff(previous.graph_runs(), next.graph_runs(), MightyGraphRun::id, same_run);
    if set.is_empty() && unset.is_empty() && logs.empty() && runs.empty() {
        return false;
    }
    out.begin_object();
    out.prop_str("id", Some(next.id()));
    if !set.is_empty() {
        out.name("set");
        out.begin_object();
        for (key, value) in set {
            out.name(key);
            out.value(value);
        }
        out.end_object();
    }
    if !unset.is_empty() {
        out.prop_strings("unset", Some(&unset));
    }
    if !logs.empty() {
        write_list(out, "logs", &logs, |out, entry| mighty_core::Wire::write(entry, out));
    }
    if !runs.empty() {
        write_list(out, "graphRuns", &runs, |out, run| run.write_with(out, false));
    }
    out.end_object();
    true
}

impl Bridge {
    pub fn reset(&mut self) {
        *self = Bridge::default();
    }

    pub fn sent_revision(&self) -> Option<u64> {
        self.sent_revision
    }

    fn remember(&mut self, state: &AppSnapshot, revision: u64, order: Vec<String>, text: String) {
        self.sent_revision = Some(revision);
        self.sent_order = order;
        self.sent_state = Some(text);
        self.sent_sessions.clear();
        for session in state.sessions() {
            self.sent_sessions.insert(session.id().to_string(), session.clone());
        }
        let present = &self.sent_sessions;
        self.sent_fields.retain(|id, _| present.contains_key(id));
    }

    /// `SendSnapshot`: the snapshot push, and the state remembered as sent.
    pub fn snapshot(&mut self, state: &AppSnapshot, revision: u64) -> String {
        let mut out = JsonOut::new();
        out.begin_object();
        out.prop_str("kind", Some("snapshot"));
        out.name("snapshot");
        out.raw(&page_snapshot(state));
        out.end_object();
        self.remember(state, revision, state.sessions().iter().map(|s| s.id().to_string()).collect(), state_json(state));
        out.finish()
    }

    /// `Flush`: the delta push for what changed since the last snapshot or delta, or None when the page holds it all already.
    pub fn flush(&mut self, state: &AppSnapshot, revision: u64) -> Option<String> {
        if self.sent_revision == Some(revision) {
            return None;
        }
        let mut changed = vec![];
        let mut order = Vec::with_capacity(state.sessions().len());
        for session in state.sessions() {
            order.push(session.id().to_string());
            match self.sent_sessions.get(session.id()) {
                Some(previous) if Arc::ptr_eq(previous, session) => {}
                _ => changed.push(session),
            }
        }
        let present: HashSet<&str> = order.iter().map(String::as_str).collect();
        // The sent ids in no particular order, as C# enumerates its dictionary's keys: in the order they were added.
        let mut removed: Vec<String> = vec![];
        for id in self.sent_order.iter().filter(|id| !present.contains(id.as_str())) {
            if self.sent_sessions.contains_key(id) && !removed.contains(id) {
                removed.push(id.clone());
            }
        }
        let text = state_json(state);
        let state_changed = self.sent_state.as_deref() != Some(text.as_str());
        let order_changed = order != self.sent_order;
        if changed.is_empty() && removed.is_empty() && !state_changed && !order_changed {
            self.remember(state, revision, order, text);
            return None;
        }
        let mut out = JsonOut::new();
        let mut panes = 0;
        out.begin_object();
        out.prop_str("kind", Some("delta"));
        out.name("sessions");
        out.begin_array();
        for session in changed {
            let Some(previous) = self.sent_sessions.get(session.id()).cloned() else {
                out.begin_object();
                out.prop_str("id", Some(session.id()));
                out.name("session");
                session.write_with(&mut out, true, false);
                out.end_object();
                panes += 1;
                continue;
            };
            let before = match self.sent_fields.get(session.id()) {
                Some((known, fields)) if Arc::ptr_eq(known, &previous) => fields.clone(),
                _ => fields(&previous),
            };
            let after = fields(session);
            if write(&mut out, &previous, &before, session, &after) {
                panes += 1;
            }
            self.sent_fields.insert(session.id().to_string(), (session.clone(), after));
        }
        out.end_array();
        out.prop_strings("removed", Some(&removed));
        if order_changed {
            out.prop_strings("order", Some(&order));
        }
        if state_changed {
            out.name("state");
            out.raw(&text);
        }
        out.end_object();
        self.remember(state, revision, order, text);
        // A pane made anew with the same values (a normalize) is no change to the page.
        if panes == 0 && removed.is_empty() && !state_changed && !order_changed {
            return None;
        }
        Some(out.finish())
    }
}

#[cfg(test)]
mod tests {
    //! The page's side, ported from `native/windows/web/src/store/delta.ts` (`applyListDelta`, `applySessionDelta`) and
    //! `store.ts` (`mergeSessions`, the delta case of `reduce`), on JSON values as the page holds them.
    use super::*;
    use mighty_core::Wire;
    use serde_json::json;

    fn apply_list(previous: &[Value], delta: &Value) -> Vec<Value> {
        let empty = delta.get("drop").and_then(Value::as_u64).unwrap_or(0) == 0
            && delta.get("replaced").and_then(Value::as_array).is_none_or(Vec::is_empty)
            && delta.get("added").and_then(Value::as_array).is_none_or(Vec::is_empty)
            && delta.get("order").is_none();
        if empty {
            return previous.to_vec();
        }
        let id = |v: &Value| v["id"].as_str().unwrap_or("").to_string();
        if let Some(order) = delta.get("order").and_then(Value::as_array) {
            let mut by_id: HashMap<String, Value> = previous.iter().map(|r| (id(r), r.clone())).collect();
            for record in delta
                .get("replaced")
                .and_then(Value::as_array)
                .into_iter()
                .flatten()
                .chain(delta.get("added").and_then(Value::as_array).into_iter().flatten())
            {
                by_id.insert(id(record), record.clone());
            }
            return order.iter().filter_map(|i| by_id.get(i.as_str().unwrap_or("")).cloned()).collect();
        }
        let drop = delta.get("drop").and_then(Value::as_u64).unwrap_or(0) as usize;
        let mut next: Vec<Value> = previous[drop.min(previous.len())..].to_vec();
        for record in delta.get("replaced").and_then(Value::as_array).into_iter().flatten() {
            if let Some(at) = next.iter().rposition(|r| id(r) == id(record)) {
                next[at] = record.clone();
            }
        }
        next.extend(delta.get("added").and_then(Value::as_array).into_iter().flatten().cloned());
        next
    }

    fn apply_session(previous: Option<&Value>, delta: &Value) -> Option<Value> {
        if let Some(session) = delta.get("session") {
            return Some(session.clone());
        }
        let previous = previous?;
        let mut next = previous.as_object().cloned().unwrap_or_default();
        if let Some(set) = delta.get("set").and_then(Value::as_object) {
            for (key, value) in set {
                next.insert(key.clone(), value.clone());
            }
        }
        for key in delta.get("unset").and_then(Value::as_array).into_iter().flatten().filter_map(Value::as_str) {
            if !matches!(key, "logs" | "graphRuns" | "id") {
                next.shift_remove(key);
            }
        }
        if let Some(logs) = delta.get("logs") {
            let old = previous["logs"].as_array().cloned().unwrap_or_default();
            next.insert("logs".into(), Value::Array(apply_list(&old, logs)));
        }
        if let Some(runs) = delta.get("graphRuns") {
            let old = previous.get("graphRuns").and_then(Value::as_array).cloned().unwrap_or_default();
            let runs = apply_list(&old, runs);
            if runs.is_empty() {
                next.shift_remove("graphRuns");
            } else {
                next.insert("graphRuns".into(), Value::Array(runs));
            }
        }
        Some(Value::Object(next))
    }

    /// `reduce`'s delta case on the page's snapshot.
    fn apply(snapshot: &Value, delta: &Value) -> Value {
        let current: Vec<Value> = snapshot["sessions"].as_array().cloned().unwrap_or_default();
        let id = |v: &Value| v["id"].as_str().unwrap_or("").to_string();
        let changed = delta["sessions"].as_array().cloned().unwrap_or_default();
        let removed: Vec<String> =
            delta["removed"].as_array().into_iter().flatten().filter_map(|v| v.as_str().map(str::to_string)).collect();
        let mut by_id: HashMap<String, Value> = current.iter().map(|s| (id(s), s.clone())).collect();
        for r in &removed {
            by_id.remove(r);
        }
        let mut added = vec![];
        for change in &changed {
            let key = change["id"].as_str().unwrap_or("").to_string();
            let before = by_id.get(&key).cloned();
            if let Some(session) = apply_session(before.as_ref(), change) {
                if before.is_none() {
                    added.push(session.clone());
                }
                by_id.insert(id(&session), session);
            }
        }
        let sessions: Vec<Value> = match delta.get("order").and_then(Value::as_array) {
            Some(order) => order.iter().filter_map(|i| by_id.get(i.as_str().unwrap_or("")).cloned()).collect(),
            None => current.iter().filter(|s| by_id.contains_key(&id(s))).map(|s| by_id[&id(s)].clone()).chain(added).collect(),
        };
        let mut next = match delta.get("state") {
            Some(state) => state.as_object().cloned().unwrap_or_default(),
            None => snapshot.as_object().cloned().unwrap_or_default(),
        };
        next.insert("sessions".into(), Value::Array(sessions));
        Value::Object(next)
    }

    /// Compared as the page reads it: objects by their fields whatever the order (JS objects keep insertion order, which no
    /// view reads), JSON numbers as numbers — and without the run fields C#'s `SameRun` does not compare (`advisor`,
    /// `configuredAdvisor`, `stepsCarried`): a change to those alone is not sent (C# behaviour, kept), so the page may hold an
    /// older value of them until something else of that run changes.
    fn page_equal(a: &Value, b: &Value) -> bool {
        fn strip(value: &Value) -> Value {
            let mut value = value.clone();
            for session in value["sessions"].as_array_mut().into_iter().flatten() {
                // The page drops an emptied run list (delta.ts applySessionDelta); a normalized state never holds one.
                if session.get("graphRuns").and_then(Value::as_array).is_some_and(Vec::is_empty)
                    && let Some(fields) = session.as_object_mut()
                {
                    fields.shift_remove("graphRuns");
                }
                for run in session.get_mut("graphRuns").and_then(Value::as_array_mut).into_iter().flatten() {
                    if let Some(run) = run.as_object_mut() {
                        for key in ["advisor", "configuredAdvisor", "stepsCarried"] {
                            run.shift_remove(key);
                        }
                    }
                }
            }
            value
        }
        strip(a) == strip(b)
    }

    // ── random states and changes ───────────────────────────────────────────────────────────────────────────────────

    struct Random(u64);
    impl Random {
        fn next(&mut self) -> u64 {
            self.0 ^= self.0 << 13;
            self.0 ^= self.0 >> 7;
            self.0 ^= self.0 << 17;
            self.0
        }
        fn below(&mut self, n: u64) -> u64 {
            self.next() % n.max(1)
        }
        fn chance(&mut self, percent: u64) -> bool {
            self.below(100) < percent
        }
    }

    fn entry(r: &mut Random, n: u64) -> Value {
        let kinds = ["user", "assistant", "output", "error"];
        let mut e = json!({"id": format!("e{n}"), "kind": (kinds[r.below(4) as usize]), "text": format!("text {} {}", n, r.below(1000)), "timestamp": "2026-11-13T10:00:00Z"});
        if r.chance(20) {
            e["activity"] = json!({"id": format!("a{n}"), "provider": "claude", "kind": "command", "state": "completed", "summary": format!("s{}", r.below(9)), "durationMs": r.below(5000) as f64 / 4.0});
        }
        e
    }

    fn run(r: &mut Random, n: u64) -> Value {
        let agents: Vec<Value> = (0..r.below(3))
            .map(|i| json!({"id": format!("ag{n}-{i}"), "title": format!("t{}", r.below(5)), "input": "in", "status": (["running", "completed"][r.below(2) as usize]), "entries": [], "outputFile": if r.chance(50) { json!("C:\\out.txt") } else { Value::Null }}))
            .collect();
        json!({"id": format!("r{n}"), "input": format!("req {n}"), "status": (["running", "completed", "error"][r.below(3) as usize]), "rootEntries": [], "agents": agents, "resultEntries": [], "provider": "claude", "advisor": if r.chance(30) { json!({"model": "opus", "calls": []}) } else { Value::Null }})
    }

    fn mutate(r: &mut Random, state: &mut Value, counter: &mut u64) {
        let sessions = state["sessions"].as_array_mut().unwrap();
        match r.below(14) {
            0 => {
                *counter += 1;
                sessions.push(json!({"id": format!("s{counter}"), "workspaceId": "w1", "title": "new", "logs": [], "createdAt": "2026-11-13T00:00:00Z"}));
            }
            1 if !sessions.is_empty() => {
                let at = r.below(sessions.len() as u64) as usize;
                sessions.remove(at);
            }
            2 if sessions.len() > 1 => {
                let a = r.below(sessions.len() as u64) as usize;
                let b = r.below(sessions.len() as u64) as usize;
                sessions.swap(a, b);
            }
            3 => state["theme"] = json!(["light", "dark"][r.below(2) as usize]),
            4 => state["sidebarWidth"] = json!(210.0 + r.below(150) as f64 + 0.5),
            _ if !sessions.is_empty() => {
                let at = r.below(sessions.len() as u64) as usize;
                let s = &mut sessions[at];
                match r.below(10) {
                    0 => s["title"] = json!(format!("title {}", r.below(50))),
                    1 => s["status"] = json!(["idle", "running", "completed", "stopped"][r.below(4) as usize]),
                    2 => {
                        *counter += 1;
                        let e = entry(r, *counter);
                        s["logs"].as_array_mut().unwrap().push(e);
                    }
                    3 => {
                        // The last entry streamed.
                        if let Some(last) = s["logs"].as_array_mut().unwrap().last_mut() {
                            last["text"] = json!(format!("{} more", last["text"].as_str().unwrap_or("")));
                        }
                    }
                    4 => {
                        // Retention trims the front.
                        let logs = s["logs"].as_array_mut().unwrap();
                        let n = r.below(3) as usize;
                        logs.drain(..n.min(logs.len()));
                    }
                    5 => {
                        let logs = s["logs"].as_array_mut().unwrap();
                        if logs.len() > 2 {
                            let at = r.below(logs.len() as u64) as usize;
                            logs.remove(at);
                        }
                    }
                    6 => {
                        *counter += 1;
                        let new_run = run(r, *counter);
                        let runs = s.as_object_mut().unwrap().entry("graphRuns").or_insert(json!([]));
                        runs.as_array_mut().unwrap().push(new_run);
                    }
                    7 => {
                        if let Some(runs) = s.get_mut("graphRuns").and_then(Value::as_array_mut)
                            && let Some(last) = runs.last_mut()
                        {
                            last["status"] = json!(["running", "completed", "error"][r.below(3) as usize]);
                            last["advisor"] = json!({"model": format!("m{}", r.below(3)), "calls": []});
                        }
                    }
                    8 => {
                        if r.chance(50) {
                            s["draft"] = json!(format!("draft {}", r.below(9)));
                        } else {
                            s.as_object_mut().unwrap().remove("draft");
                            s["resumeId"] = json!(format!("resume-{}", r.below(9)));
                        }
                    }
                    _ => {
                        if let Some(runs) = s.get_mut("graphRuns").and_then(Value::as_array_mut)
                            && !runs.is_empty()
                        {
                            runs.remove(0);
                        }
                    }
                }
            }
            _ => {}
        }
    }

    fn read(state: &Value) -> AppSnapshot {
        AppSnapshot::read(state).unwrap()
    }

    /// Random edits, each state read anew (every pane a new object, as a normalize makes them): the page that applies each delta
    /// holds exactly the next snapshot, and a state that did not change sends nothing.
    #[test]
    fn applying_the_deltas_reaches_every_snapshot() {
        for seed in 1..=40u64 {
            let mut r = Random(0x2545_F491_4F6C_DD1D ^ seed.wrapping_mul(0x9E37_79B9_7F4A_7C15));
            let mut counter = 0;
            let mut state = json!({"version": 1, "workspaces": [{"id": "w1", "name": "W", "path": "C:\\w", "createdAt": "2026-11-13T00:00:00Z"}], "sessions": []});
            for _ in 0..3 {
                mutate(&mut r, &mut state, &mut counter);
            }
            let mut bridge = Bridge::default();
            let first = read(&state);
            let pushed: Value = serde_json::from_str(&bridge.snapshot(&first, 0)).unwrap();
            let mut page = pushed["snapshot"].clone();
            for step in 1..=60u64 {
                let unchanged = r.chance(10);
                if !unchanged {
                    mutate(&mut r, &mut state, &mut counter);
                }
                let next = read(&state);
                let expected: Value = serde_json::from_str(&page_snapshot(&next)).unwrap();
                if let Some(text) = bridge.flush(&next, step) {
                    let delta: Value = serde_json::from_str(&text).unwrap();
                    assert_eq!(delta["kind"], "delta");
                    page = apply(&page, &delta);
                }
                assert!(page_equal(&page, &expected), "seed {seed} step {step}: the page holds\n{page}\nnot\n{expected}");
                if unchanged {
                    // Read anew but the same: no delta.
                    assert!(bridge.flush(&read(&state), step + 1000).is_none(), "seed {seed} step {step}: an unchanged state sent a delta");
                }
            }
        }
    }

    #[test]
    fn a_streamed_line_sends_only_that_line() {
        let state = json!({"version": 1, "workspaces": [{"id": "w1", "path": "C:\\w", "createdAt": "t"}], "sessions": [{"id": "s1", "workspaceId": "w1", "createdAt": "t", "logs": [
            {"id": "e1", "kind": "user", "text": "hi", "timestamp": "t"}, {"id": "e2", "kind": "assistant", "text": "hel", "timestamp": "t"}]}]});
        let mut bridge = Bridge::default();
        bridge.snapshot(&read(&state), 0);
        let mut next = state.clone();
        next["sessions"][0]["logs"][1]["text"] = json!("hello");
        let delta = bridge.flush(&read(&next), 1).unwrap();
        assert_eq!(
            delta,
            r#"{"kind":"delta","sessions":[{"id":"s1","logs":{"replaced":[{"id":"e2","kind":"assistant","text":"hello","timestamp":"t"}]}}],"removed":[]}"#
        );
    }

    #[test]
    fn output_files_never_reach_the_page() {
        let state = json!({"version": 1, "workspaces": [{"id": "w1", "path": "C:\\w", "createdAt": "t"}], "sessions": [{"id": "s1", "workspaceId": "w1", "createdAt": "t",
            "graphRuns": [{"id": "r1", "agents": [{"id": "a1", "outputFile": "C:\\secret.txt"}]}]}]});
        let mut bridge = Bridge::default();
        assert!(!bridge.snapshot(&read(&state), 0).contains("secret"));
        let mut next = state.clone();
        next["sessions"][0]["graphRuns"][0]["status"] = json!("completed");
        let delta = bridge.flush(&read(&next), 1).unwrap();
        assert!(delta.contains("\"graphRuns\":{\"replaced\"") && !delta.contains("secret"), "{delta}");
    }

    /// The C# bridge itself (`tools/parity`'s `delta` command, WebDelta.cs compiled in) over a random sequence: the same pushes,
    /// byte for byte. Skipped without the .NET SDK (`MIGHTY_PARITY=require` makes that a failure).
    #[test]
    fn the_deltas_are_the_csharp_bridges() {
        let tauri = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("..");
        let tool = tauri.join("target/parity-cs/parity.dll");
        let built = std::process::Command::new("dotnet")
            .arg("build")
            .arg(tauri.join("tools/parity/Parity.csproj"))
            .args(["-c", "Release", "-o"])
            .arg(tauri.join("target/parity-cs"))
            .args(["--nologo", "-v", "quiet"])
            .status();
        if !matches!(built, Ok(status) if status.success()) {
            assert!(std::env::var("MIGHTY_PARITY").as_deref() != Ok("require"), "the C# parity tool did not build");
            eprintln!("skipped: the C# parity tool did not build");
            return;
        }
        let work = std::env::temp_dir().join(format!("mighty-delta-{}", std::process::id()));
        std::fs::create_dir_all(&work).unwrap();
        for seed in 1..=12u64 {
            let mut r = Random(0x5DEE_CE66_D1CE_4E5B ^ seed.wrapping_mul(0x9E37_79B9_7F4A_7C15));
            let mut counter = 0;
            let mut state = json!({"version": 1, "workspaces": [{"id": "w1", "name": "W", "path": "C:\\w", "createdAt": "2026-11-13T00:00:00Z"}], "sessions": []});
            let mut lines = vec![];
            for step in 0..50 {
                if step == 0 || !r.chance(10) {
                    mutate(&mut r, &mut state, &mut counter);
                }
                // Each state as the app holds it: read and written by the port (the C# side reads the same text).
                lines.push(read(&state).to_json());
            }
            let input = work.join(format!("states-{seed}.jsonl"));
            let output = work.join(format!("deltas-{seed}.cs.jsonl"));
            std::fs::write(&input, lines.join("\n") + "\n").unwrap();
            let ran = std::process::Command::new("dotnet").arg(&tool).arg("delta").arg(&input).arg(&output).status().unwrap();
            assert!(ran.success());
            let expected = std::fs::read_to_string(&output).unwrap();
            let mut bridge = Bridge::default();
            let mut ours = vec![];
            for (step, line) in lines.iter().enumerate() {
                let state = read(&serde_json::from_str(line).unwrap());
                ours.push(if step == 0 { bridge.snapshot(&state, 0) } else { bridge.flush(&state, step as u64).unwrap_or_default() });
            }
            for (step, (a, b)) in expected.lines().zip(ours.iter()).enumerate() {
                assert_eq!(a, b, "seed {seed} push {step}");
            }
            assert_eq!(expected.lines().count(), ours.len(), "seed {seed}");
        }
        std::fs::remove_dir_all(work).ok();
    }
}
