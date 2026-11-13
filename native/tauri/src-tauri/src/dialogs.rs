//! The dialogs the window's flows ask the page to draw (C# `OpenWebDialog`, `CloseWebDialog`, `dialogAnswer`,
//! MainWindow.WebShell.Shell.cs:169-216): a confirmation, the rename sheet, the resume sheets and the two create sheets. A flow pushes
//! `{kind:'dialog', id, dialog}` and waits for the page's `dialogAnswer{id, answer}`; the host can close one itself
//! (`{kind:'dialogClose', id}`, the flow's answer then null) or change a resume choice's checking state (`{kind:'dialogUpdate', id,
//! checking}`).
//!
//! Unlike C#, whose flows wait for ever once the page loads again (its dialogs are never sent again), every dialog still open is sent
//! again, oldest first and as it stands now, when the page calls `ready`: the reloaded page draws it and its answer reaches the flow.

use std::sync::{Arc, Mutex, MutexGuard};

use indexmap::IndexMap;
use serde_json::{Value, json};
use tauri::Manager;
use tokio::sync::oneshot;

use crate::host::{Host, Registry};

/// Where the dialogs' pushes go (the host's, dropped until the page is ready).
pub type Push = Arc<dyn Fn(&str, Value) + Send + Sync>;

struct Open {
    dialog: Value,
    answer: oneshot::Sender<Value>,
}

#[derive(Default)]
struct State {
    next: u64,
    open: IndexMap<u64, Open>,
}

/// The dialogs open now.
pub struct Dialogs {
    state: Mutex<State>,
    push: Push,
}

impl Dialogs {
    pub fn new(push: Push) -> Dialogs {
        Dialogs { state: Mutex::default(), push }
    }

    fn state(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// `OpenWebDialog`: the dialog pushed to the page; the receiver ends with its answer (null when it was closed by the host or the
    /// app ended).
    pub fn open(&self, dialog: Value) -> (u64, oneshot::Receiver<Value>) {
        let (sender, receiver) = oneshot::channel();
        let id = {
            let mut state = self.state();
            state.next += 1;
            let id = state.next;
            state.open.insert(id, Open { dialog: dialog.clone(), answer: sender });
            id
        };
        (self.push)("dialog", json!({"kind": "dialog", "id": id, "dialog": dialog}));
        (id, receiver)
    }

    /// `CloseWebDialog`: its flow's answer is null and the page takes it away.
    pub fn close(&self, id: u64) {
        if let Some(open) = self.state().open.shift_remove(&id) {
            let _ = open.answer.send(Value::Null);
        }
        (self.push)("dialogClose", json!({"kind": "dialogClose", "id": id}));
    }

    /// The `dialogUpdate` a resume choice takes when its look-up ended: kept in the dialog, so a page that loads again draws it so.
    pub fn update(&self, id: u64, checking: bool) {
        if let Some(open) = self.state().open.get_mut(&id) {
            open.dialog["checking"] = json!(checking);
        }
        (self.push)("dialogUpdate", json!({"kind": "dialogUpdate", "id": id, "checking": checking}));
    }

    /// `dialogAnswer{id, answer}`: the flow waiting on it goes on; an id no flow waits on is nothing.
    pub fn answer(&self, id: u64, answer: Value) {
        if let Some(open) = self.state().open.shift_remove(&id) {
            let _ = open.answer.send(answer);
        }
    }

    /// Whether a dialog with this id is open (C# `webDialogs.ContainsKey`).
    pub fn is_open(&self, id: u64) -> bool {
        self.state().open.contains_key(&id)
    }

    /// How many dialogs wait for their answer (the smoke's reload step).
    pub fn open_count(&self) -> usize {
        self.state().open.len()
    }

    /// The page called `ready`: every dialog still open, oldest first, as it stands.
    pub fn resend(&self) {
        let open: Vec<(u64, Value)> = self.state().open.iter().map(|(id, open)| (*id, open.dialog.clone())).collect();
        for (id, dialog) in open {
            (self.push)("dialog", json!({"kind": "dialog", "id": id, "dialog": dialog}));
        }
    }
}

/// The host's dialogs, made the first time a flow asks.
pub fn of(host: &Arc<Host>) -> Arc<Dialogs> {
    if let Some(dialogs) = host.app.try_state::<Arc<Dialogs>>() {
        return dialogs.inner().clone();
    }
    let owner = Arc::downgrade(host);
    let push: Push = Arc::new(move |kind, message| {
        if let Some(host) = owner.upgrade() {
            let view = host.view();
            host.push(&view, kind, message.to_string());
        }
    });
    // Two first asks at once: the first managed one wins and both use it.
    host.app.manage(Arc::new(Dialogs::new(push)));
    host.app.state::<Arc<Dialogs>>().inner().clone()
}

/// What the page's `ready` brings back that a reload lost: the open dialogs (and the text setting, `frame::page_ready`).
pub fn page_ready(host: &Arc<Host>) {
    if let Some(dialogs) = host.app.try_state::<Arc<Dialogs>>() {
        dialogs.resend();
    }
}

/// C#'s anonymous dialog objects as `Wire.Json` writes them: a member whose value is null is left out.
pub fn without_nulls(value: Value) -> Value {
    match value {
        Value::Object(map) => Value::Object(map.into_iter().filter(|(_, v)| !v.is_null()).collect()),
        other => other,
    }
}

pub fn register(methods: &mut Registry) {
    methods.add("dialogAnswer", |host: Arc<Host>, args| async move {
        // C# `ArgNumber(args, "id") is { } id`: a JSON number, taken as a whole one.
        if let Some(id) = args.get("id").and_then(Value::as_f64).filter(|id| id.is_finite() && *id >= 0.0) {
            of(&host).answer(id as u64, args.get("answer").cloned().unwrap_or(Value::Null));
        }
        Ok(Value::Null)
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    fn dialogs() -> (Dialogs, Arc<Mutex<Vec<Value>>>) {
        let pushed = Arc::new(Mutex::new(vec![]));
        let sink = pushed.clone();
        (Dialogs::new(Arc::new(move |_, message| sink.lock().unwrap().push(message))), pushed)
    }

    #[tokio::test]
    async fn a_dialog_is_pushed_and_its_flow_takes_the_answer() {
        let (dialogs, pushed) = dialogs();
        let (id, answer) = dialogs.open(json!({"type": "confirm", "title": "Remove?"}));
        assert_eq!(pushed.lock().unwrap()[0], json!({"kind": "dialog", "id": id, "dialog": {"type": "confirm", "title": "Remove?"}}));
        assert!(dialogs.is_open(id));
        dialogs.answer(id + 100, json!(true));
        dialogs.answer(id, json!(true));
        assert_eq!(answer.await.unwrap(), json!(true));
        assert!(!dialogs.is_open(id));
        dialogs.answer(id, json!(false));
    }

    #[tokio::test]
    async fn a_dialog_the_host_closes_answers_null() {
        let (dialogs, pushed) = dialogs();
        let (id, answer) = dialogs.open(json!({"type": "resumeChoice", "checking": true}));
        dialogs.close(id);
        assert_eq!(answer.await.unwrap(), Value::Null);
        assert_eq!(pushed.lock().unwrap().last().unwrap(), &json!({"kind": "dialogClose", "id": id}));
    }

    /// The fix of C#'s latent bug: a page that loads again gets every open dialog again, as it stands, and its answer still reaches
    /// the waiting flow.
    #[tokio::test]
    async fn open_dialogs_are_sent_again_when_the_page_is_ready_again() {
        let (dialogs, pushed) = dialogs();
        let (first, _keep) = dialogs.open(json!({"type": "rename", "current": "a"}));
        let (second, answer) = dialogs.open(json!({"type": "resumeChoice", "checking": true}));
        dialogs.update(second, false);
        let (closed, _gone) = dialogs.open(json!({"type": "confirm"}));
        dialogs.close(closed);
        pushed.lock().unwrap().clear();
        dialogs.resend();
        assert_eq!(
            *pushed.lock().unwrap(),
            [
                json!({"kind": "dialog", "id": first, "dialog": {"type": "rename", "current": "a"}}),
                json!({"kind": "dialog", "id": second, "dialog": {"type": "resumeChoice", "checking": false}}),
            ]
        );
        dialogs.answer(second, json!("resume"));
        assert_eq!(answer.await.unwrap(), json!("resume"));
    }

    #[test]
    fn nulls_are_left_out_as_wire_json_leaves_them() {
        assert_eq!(without_nulls(json!({"a": 1, "b": null})), json!({"a": 1}));
    }
}
