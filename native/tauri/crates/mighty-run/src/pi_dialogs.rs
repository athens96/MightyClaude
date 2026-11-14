//! C# `PiDialogChannel` (PiDialogChannel.cs; M/MightyCore/AgentQuestions.swift `PiDialogChannel`): the dialogs a pi extension opens
//! in RPC mode (`extension_ui_request`, pi's docs/rpc-extension-ui.md). `select`, `confirm` and `input` become the pane's question
//! card, and the answer leaves as an `extension_ui_response`. The app's own `ask_user` tool (the shipped `pi-ask-user.js`) asks
//! through `select` and `input` and takes a typed answer, or several picks, in place of one choice; any other extension's `select`
//! is answered with one of its options only. An `editor` dialog (a text to edit) is not a question: the parser cancels it.
//!
//! C# runs the callbacks under its lock (and the parser's, which hands it the dialogs); here they run after the channel's lock is
//! released, in C#'s order, and the parser hands a dialog over after its own lock is released, so a card's answer and its tool row
//! never wait on each other.

use std::sync::{Arc, Mutex};

use indexmap::IndexMap;
use mighty_core::{time, wire};
use serde_json::{Value, json};

use crate::activity::clean;
use crate::meta;
use crate::permissions::{ActivityHook, Emit, Message, ToolPermissionRequest};
use crate::presentation::strings as copy;
use crate::questionnaire::{UserQuestionAnswer, agent};
use crate::strings;
use crate::text_json;
use crate::{Result, RunError, lock};

/// `PiDialogChannel.MaximumPending`.
pub const MAXIMUM_PENDING: usize = 16;

/// `PiDialogChannel.Asking(ToolCallId, Multiple)`: the `ask_user` call a dialog belongs to, when its title is that call's question.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Asking {
    pub tool_call_id: String,
    pub multiple: bool,
}

/// A dialog pi gives a deadline: its card's id and the seconds (C#'s `timer`).
pub type Timer = Arc<dyn Fn(&str, f64) + Send + Sync>;

/// The callbacks a channel reports through (C#'s constructor arguments).
#[derive(Clone)]
pub struct PiDialogHooks {
    /// One `extension_ui_response` line to pi's stdin.
    pub write: Arc<dyn Fn(&str) + Send + Sync>,
    /// A card waiting, or settled.
    pub emit: Emit,
    /// The `ask_user` row's state (the parser owns the row).
    pub activity: ActivityHook,
    pub warning: Message,
    /// A dialog pi gives a deadline: its card's id and the seconds (pi answers it itself when they pass).
    pub timer: Option<Timer>,
}

enum Effect {
    Emit(ToolPermissionRequest),
    Activity(ToolPermissionRequest, String),
    Warning(String),
    Timer(String, f64),
}

struct Pending {
    dialog_id: String,
    method: String,
    asking: Option<Asking>,
    display: ToolPermissionRequest,
}

#[derive(Default)]
struct State {
    pending: IndexMap<String, Pending>,
    closed: bool,
}

/// C# `PiDialogChannel`.
pub struct PiDialogChannel {
    run_id: String,
    hooks: PiDialogHooks,
    state: Mutex<State>,
}

impl PiDialogChannel {
    /// `run_id`: the pane, which the cards carry as their run.
    pub fn new(run_id: &str, hooks: PiDialogHooks) -> PiDialogChannel {
        PiDialogChannel { run_id: run_id.into(), hooks, state: Mutex::new(State::default()) }
    }

    fn run(&self, effects: Vec<Effect>) {
        for effect in effects {
            match effect {
                Effect::Emit(value) => (self.hooks.emit)(value),
                Effect::Activity(value, state) => (self.hooks.activity)(&value, &state),
                Effect::Warning(message) => (self.hooks.warning)(&message),
                Effect::Timer(id, seconds) => {
                    if let Some(timer) = &self.hooks.timer {
                        timer(&id, seconds);
                    }
                }
            }
        }
    }

    /// `Receive`: one dialog shown as the card; false when the card cannot ask it (the caller cancels it).
    pub fn receive(&self, value: &Value, asking: Option<Asking>) -> bool {
        let mut effects = Vec::new();
        let shown = self.receive_locked(&mut lock(&self.state), &mut effects, value, asking);
        self.run(effects);
        shown
    }

    fn receive_locked(&self, state: &mut State, effects: &mut Vec<Effect>, value: &Value, asking: Option<Asking>) -> bool {
        if state.closed || state.pending.len() >= MAXIMUM_PENDING {
            return false;
        }
        let Some(dialog_id) = meta::text(value, "id").filter(|id| !id.is_empty() && id.len() <= 256) else { return false };
        let Some(method) = meta::text(value, "method") else { return false };
        let Some(title) = agent::text(meta::text(value, "title"), 8_192, false) else { return false };
        let mut item = agent::Item {
            header: strings::get("questionnaire.header.agent"),
            question: title.clone(),
            multi_select: false,
            options: vec![],
        };
        match method {
            "select" => {
                let raw: &[Value] = match meta::prop(value, "options") {
                    Some(Value::Array(list)) => list,
                    _ => &[],
                };
                // The option itself is the answer pi reads: one the card would have to shorten is not asked.
                let mut options = Vec::new();
                for option in raw {
                    let Value::String(given) = option else { return false };
                    match agent::text(Some(given), 1_024, true) {
                        Some(shown) if shown == *given => options.push((shown, String::new())),
                        _ => return false,
                    }
                }
                if options.is_empty() {
                    return false;
                }
                item.options = options;
                item.multi_select = asking.as_ref().is_some_and(|a| a.multiple);
            }
            "confirm" => {
                if let Some(message) = agent::text(meta::text(value, "message"), 8_192, false) {
                    item.header = clean(Some(&title), 256, true);
                    item.question = message;
                }
                item.options.push((strings::get("questionnaire.option.yes"), String::new()));
                item.options.push((strings::get("questionnaire.option.no"), String::new()));
            }
            "input" => {}
            _ => return false,
        }
        let tool_use_id = match &asking {
            Some(asking) => asking.tool_call_id.clone(),
            None => format!("pi-dialog-{dialog_id}"),
        };
        let Some(request) = agent::request(&wire::new_id(), &self.run_id, &tool_use_id, &[item]) else { return false };
        let display = ToolPermissionRequest { received_at: Some(time::now_o()), ..request };
        let has_asking = asking.is_some();
        state
            .pending
            .insert(display.id.clone(), Pending { dialog_id: dialog_id.into(), method: method.into(), asking, display: display.clone() });
        if has_asking {
            effects.push(Effect::Activity(display.clone(), "waiting".into()));
        }
        effects.push(Effect::Emit(display.clone()));
        if let Some(milliseconds) = meta::number(value, "timeout").filter(|ms| *ms > 0.0) {
            effects.push(Effect::Timer(display.id, milliseconds / 1_000.0));
        }
        true
    }

    /// `AnswerQuestions`: the card's answer as the dialog's value (`confirmed` for a confirm).
    pub fn answer_questions(&self, request_id: &str, answers: &IndexMap<String, UserQuestionAnswer>) -> Result<()> {
        let ask = {
            let mut state = lock(&self.state);
            let Some(asked) = state.pending.get(request_id).filter(|_| !state.closed) else {
                return Err(RunError::State(strings::get("permission.error.questionGone")));
            };
            let (picked, typed) = agent::answers(&asked.display, answers)?.into_values().next().unwrap_or_default();
            let mut response = json!({"type": "extension_ui_response", "id": asked.dialog_id});
            let choices_only = || RunError::Invalid(strings::get("questionnaire.error.choicesOnly"));
            match asked.method.as_str() {
                "confirm" => {
                    let first = picked.first().filter(|_| typed.is_empty()).ok_or_else(choices_only)?;
                    response["confirmed"] = Value::Bool(*first == strings::get("questionnaire.option.yes"));
                }
                "select" => {
                    // Only the app's own tool reads anything but one of its options.
                    if asked.asking.is_none() && (!typed.is_empty() || picked.len() != 1) {
                        return Err(choices_only());
                    }
                    let mut parts = picked;
                    if !typed.is_empty() {
                        parts.push(typed);
                    }
                    response["value"] = Value::String(parts.join(", "));
                }
                _ => {
                    if typed.is_empty() {
                        return Err(RunError::Invalid(strings::get("questionnaire.error.missingAnswer")));
                    }
                    response["value"] = Value::String(typed);
                }
            }
            let ask = state.pending.shift_remove(request_id).expect("the pending dialog");
            self.send(&response);
            ask
        };
        let display = ToolPermissionRequest { state: "answered".into(), ..ask.display };
        if ask.asking.is_some() {
            (self.hooks.activity)(&display, "running");
        }
        (self.hooks.emit)(display);
        Ok(())
    }

    /// `Respond`: the card's Cancel; pi reads the dialog as dismissed. A dialog is never "allowed".
    pub fn respond(&self, request_id: &str, allow: bool) -> Result<()> {
        let ask = {
            let mut state = lock(&self.state);
            if state.closed || !state.pending.contains_key(request_id) {
                return Err(RunError::State(copy::already_settled()));
            }
            if allow {
                return Err(RunError::State(strings::get("permission.error.noAnswers")));
            }
            let ask = state.pending.shift_remove(request_id).expect("the pending dialog");
            self.send(&json!({"type": "extension_ui_response", "id": ask.dialog_id, "cancelled": true}));
            ask
        };
        let display = ToolPermissionRequest { state: "denied".into(), ..ask.display };
        if ask.asking.is_some() {
            (self.hooks.activity)(&display, "running");
        }
        (self.hooks.emit)(display);
        Ok(())
    }

    /// `TimedOut`: pi answered the dialog itself when its deadline passed.
    pub fn timed_out(&self, request_id: &str) {
        let mut effects = Vec::new();
        {
            let mut state = lock(&self.state);
            let Some(summary) = state.pending.get(request_id).map(|p| p.display.summary.clone()) else { return };
            Self::withdraw(&mut state, &mut effects, request_id);
            let title = clean(Some(&summary), 256, true);
            effects.push(Effect::Warning(strings::get_with("run.notice.piDialogTimedOut", &[("title", &title)])));
        }
        self.run(effects);
    }

    /// `ToolEnded`: the tool that asked has ended (pi aborted it); its card has nothing left to answer.
    pub fn tool_ended(&self, tool_call_id: &str) {
        let mut effects = Vec::new();
        {
            let mut state = lock(&self.state);
            let ended: Vec<String> = state
                .pending
                .iter()
                .filter(|(_, p)| p.asking.as_ref().is_some_and(|a| a.tool_call_id == tool_call_id))
                .map(|(id, _)| id.clone())
                .collect();
            for id in ended {
                Self::withdraw(&mut state, &mut effects, &id);
            }
        }
        self.run(effects);
    }

    /// `CancelAll`: the run ended; every card is withdrawn.
    pub fn cancel_all(&self) {
        let mut effects = Vec::new();
        {
            let mut state = lock(&self.state);
            if state.closed {
                return;
            }
            state.closed = true;
            let ids: Vec<String> = state.pending.keys().cloned().collect();
            for id in ids {
                Self::withdraw(&mut state, &mut effects, &id);
            }
        }
        self.run(effects);
    }

    fn withdraw(state: &mut State, effects: &mut Vec<Effect>, id: &str) {
        if let Some(ask) = state.pending.shift_remove(id) {
            effects.push(Effect::Emit(ToolPermissionRequest { state: "cancelled".into(), ..ask.display }));
        }
    }

    fn send(&self, value: &Value) {
        (self.hooks.write)(&format!("{}\n", text_json::relaxed(value, false)));
    }
}
