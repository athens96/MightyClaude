//! The act table: what `nativeAction{view,id,op,value}` does to the control an id names (C# `WebNativeReader.Act`, WebNative.cs).
//!
//! The reader fills the table as it reads (C# `Begin` then `Id`): only what the last read reached can be acted on, as each control
//! stood when it was read. [`Acts::resolve`] answers what the input does without running anything, so the caller can let go of its
//! locks before the handler runs ([`Outcome::run`]).
//!
//! | op | applies to |
//! |---|---|
//! | `invoke` | a link with an http(s) address (opened); an enabled button (its Click); an enabled toggle or plain menu row |
//! | `toggle` | an enabled radio button (checked); an enabled toggle button, check box or segment; an enabled switch; an expander |
//! | `select` | an enabled pop-up, an int32 0 ≤ i < count; a list, −1 ≤ i < count |
//! | `text` | an enabled field that is not read-only, a string (a password box too) |
//! | `value` | an enabled slider, a number held to its range |
//!
//! A control's own event fires only on a change (a radio button already checked, the same row picked again, the same words): its
//! handler runs only then, and the input still answers true. Anything else answers false.

use std::collections::HashMap;

use serde_json::Value;

use crate::ui::{Handler, Input, Local};

/// One control the last read reached, as it stood.
pub enum Target {
    Link {
        uri: Option<String>,
    },
    Button {
        enabled: bool,
        on: Option<Handler>,
    },
    MenuItem {
        enabled: bool,
        on: Option<Handler>,
    },
    ToggleMenuItem {
        enabled: bool,
        checked: bool,
        on: Option<Handler>,
    },
    Radio {
        enabled: bool,
        on: bool,
        handler: Option<Handler>,
    },
    /// A ToggleButton: the form's switch, a check box, a segment.
    Toggle {
        enabled: bool,
        on: bool,
        handler: Option<Handler>,
    },
    Switch {
        enabled: bool,
        on: bool,
        handler: Option<Handler>,
    },
    Expander {
        open: Local<bool>,
    },
    Combo {
        enabled: bool,
        index: i32,
        count: usize,
        handler: Option<Handler>,
    },
    List {
        index: i32,
        count: usize,
        handler: Option<Handler>,
    },
    Field {
        enabled: bool,
        readonly: bool,
        text: String,
        handler: Option<Handler>,
    },
    Password {
        enabled: bool,
        text: String,
        handler: Option<Handler>,
    },
    Slider {
        enabled: bool,
        value: f64,
        min: f64,
        max: f64,
        handler: Option<Handler>,
    },
}

/// The table of one read.
#[derive(Default)]
pub struct Acts {
    targets: HashMap<i64, Target>,
}

/// What an input does.
pub enum Outcome {
    /// Nothing: the id is gone, or the control does not take that input (false).
    Refused,
    /// Taken, with nothing to run (a button without a handler, a radio already checked, the same row again).
    Done,
    /// Taken: run the handler with this input.
    Call(Handler, Input),
    /// Taken: open the address in the user's browser.
    Open(String),
    /// Taken: flip the expander.
    Flip(Local<bool>),
}

impl Outcome {
    /// Whether the input was taken (`nativeAction`'s answer).
    pub fn taken(&self) -> bool {
        !matches!(self, Outcome::Refused)
    }

    /// Does it: the handler runs, the expander flips, the address goes to `open_url`. Answers [`Outcome::taken`].
    pub fn run(self, open_url: impl FnOnce(&str)) -> bool {
        match self {
            Outcome::Refused => false,
            Outcome::Done => true,
            Outcome::Call(handler, input) => {
                handler(input);
                true
            }
            Outcome::Open(url) => {
                open_url(&url);
                true
            }
            Outcome::Flip(open) => {
                open.update(|open| *open = !*open);
                true
            }
        }
    }
}

fn call(handler: &Option<Handler>, input: Input) -> Outcome {
    match handler {
        Some(handler) => Outcome::Call(handler.clone(), input),
        None => Outcome::Done,
    }
}

/// `JsonElement.TryGetInt32`: an integer literal within int32 (a fraction or an exponent is not one).
fn int32(value: &Value) -> Option<i32> {
    value.as_i64().and_then(|v| i32::try_from(v).ok())
}

/// C#'s `uri.Scheme is "http" or "https"` for a `NavigateUri`.
fn web_address(uri: &str) -> bool {
    let lower = uri.to_ascii_lowercase();
    lower.starts_with("http://") || lower.starts_with("https://")
}

impl Acts {
    pub fn clear(&mut self) {
        self.targets.clear();
    }

    pub fn insert(&mut self, id: i64, target: Target) {
        self.targets.insert(id, target);
    }

    pub fn len(&self) -> usize {
        self.targets.len()
    }

    pub fn is_empty(&self) -> bool {
        self.targets.is_empty()
    }

    /// The ids of the last read, smallest first (tests, the smoke).
    pub fn ids(&self) -> Vec<i64> {
        let mut ids: Vec<i64> = self.targets.keys().copied().collect();
        ids.sort_unstable();
        ids
    }

    /// What `op` with `value` does to control `id` (WebNative.cs `Act`).
    pub fn resolve(&self, id: i64, op: &str, value: &Value) -> Outcome {
        let Some(target) = self.targets.get(&id) else { return Outcome::Refused };
        match (op, target) {
            ("invoke", Target::Link { uri: Some(uri) }) if web_address(uri) => Outcome::Open(uri.clone()),
            ("invoke", Target::Button { enabled: true, on }) => call(on, Input::Invoke),
            ("invoke", Target::ToggleMenuItem { enabled: true, checked, on }) => call(on, Input::Toggle(!checked)),
            ("invoke", Target::MenuItem { enabled: true, on }) => call(on, Input::Invoke),
            ("toggle", Target::Radio { enabled: true, on, handler }) => {
                if *on {
                    Outcome::Done
                } else {
                    call(handler, Input::Toggle(true))
                }
            }
            ("toggle", Target::Toggle { enabled: true, on, handler }) => call(handler, Input::Toggle(!on)),
            ("toggle", Target::Switch { enabled: true, on, handler }) => call(handler, Input::Toggle(!on)),
            ("toggle", Target::Expander { open }) => Outcome::Flip(open.clone()),
            ("select", Target::Combo { enabled: true, index, count, handler }) => match int32(value) {
                Some(row) if row >= 0 && (row as usize) < *count => {
                    if row == *index {
                        Outcome::Done
                    } else {
                        call(handler, Input::Select(row))
                    }
                }
                _ => Outcome::Refused,
            },
            ("select", Target::List { index, count, handler }) => match int32(value) {
                Some(row) if row >= -1 && (row < 0 || (row as usize) < *count) => {
                    if row == *index {
                        Outcome::Done
                    } else {
                        call(handler, Input::Select(row))
                    }
                }
                _ => Outcome::Refused,
            },
            ("text", Target::Field { enabled: true, readonly: false, text, handler }) => match value.as_str() {
                Some(words) if words == text => Outcome::Done,
                Some(words) => call(handler, Input::Text(words.to_string())),
                None => Outcome::Refused,
            },
            ("text", Target::Password { enabled: true, text, handler }) => match value.as_str() {
                Some(words) if words == text => Outcome::Done,
                Some(words) => call(handler, Input::Text(words.to_string())),
                None => Outcome::Refused,
            },
            ("value", Target::Slider { enabled: true, value: current, min, max, handler }) => match value.as_f64() {
                Some(number) if value.is_number() => {
                    let held = number.clamp(*min, *max);
                    if held == *current { Outcome::Done } else { call(handler, Input::Value(held)) }
                }
                _ => Outcome::Refused,
            },
            _ => Outcome::Refused,
        }
    }
}

#[cfg(test)]
mod tests {
    use std::sync::{Arc, Mutex};

    use serde_json::json;

    use super::*;
    use crate::ui::handler;

    fn recorder() -> (Handler, Arc<Mutex<Vec<Input>>>) {
        let seen = Arc::new(Mutex::new(vec![]));
        let into = seen.clone();
        (handler(move |input| into.lock().unwrap().push(input)), seen)
    }

    fn run(acts: &Acts, id: i64, op: &str, value: Value) -> bool {
        acts.resolve(id, op, &value).run(|_| {})
    }

    /// WebNative.cs:378-425, row by row: each op on the controls it applies to, refused on the others and on disabled ones.
    #[test]
    fn the_act_matrix() {
        let (on, seen) = recorder();
        let mut acts = Acts::default();
        acts.insert(1, Target::Button { enabled: true, on: Some(on.clone()) });
        acts.insert(2, Target::Button { enabled: false, on: Some(on.clone()) });
        acts.insert(3, Target::Link { uri: Some("https://example.com/".into()) });
        acts.insert(4, Target::Link { uri: None });
        acts.insert(5, Target::Radio { enabled: true, on: false, handler: Some(on.clone()) });
        acts.insert(6, Target::Radio { enabled: true, on: true, handler: Some(on.clone()) });
        acts.insert(7, Target::Toggle { enabled: true, on: true, handler: Some(on.clone()) });
        acts.insert(8, Target::Switch { enabled: true, on: false, handler: Some(on.clone()) });
        acts.insert(9, Target::Combo { enabled: true, index: 0, count: 3, handler: Some(on.clone()) });
        acts.insert(10, Target::List { index: 1, count: 2, handler: Some(on.clone()) });
        acts.insert(11, Target::Field { enabled: true, readonly: false, text: "a".into(), handler: Some(on.clone()) });
        acts.insert(12, Target::Field { enabled: true, readonly: true, text: "a".into(), handler: Some(on.clone()) });
        acts.insert(13, Target::Slider { enabled: true, value: 1.0, min: 0.0, max: 10.0, handler: Some(on.clone()) });
        acts.insert(14, Target::ToggleMenuItem { enabled: true, checked: false, on: Some(on.clone()) });
        acts.insert(15, Target::MenuItem { enabled: false, on: Some(on.clone()) });
        let open = Local::new(false);
        acts.insert(16, Target::Expander { open: open.clone() });

        assert!(run(&acts, 1, "invoke", Value::Null));
        assert!(!run(&acts, 2, "invoke", Value::Null), "a disabled button");
        assert!(!run(&acts, 1, "toggle", Value::Null), "a button does not toggle");
        let mut opened = None;
        assert!(acts.resolve(3, "invoke", &Value::Null).run(|url| opened = Some(url.to_string())));
        assert_eq!(opened.as_deref(), Some("https://example.com/"));
        assert!(!run(&acts, 4, "invoke", Value::Null), "a link without a web address");
        assert!(run(&acts, 5, "toggle", Value::Null));
        assert!(run(&acts, 6, "toggle", Value::Null), "a checked radio button takes it without an event");
        assert!(run(&acts, 7, "toggle", Value::Null));
        assert!(run(&acts, 8, "toggle", Value::Null));
        assert!(run(&acts, 9, "select", json!(2)));
        assert!(!run(&acts, 9, "select", json!(3)), "past the rows");
        assert!(!run(&acts, 9, "select", json!(-1)), "a pop-up has no −1");
        assert!(!run(&acts, 9, "select", json!(1.0)), "TryGetInt32 refuses a fraction's form");
        assert!(!run(&acts, 9, "select", json!("1")));
        assert!(run(&acts, 10, "select", json!(-1)), "a list takes −1");
        assert!(run(&acts, 11, "text", json!("ab")));
        assert!(!run(&acts, 11, "text", json!(5)));
        assert!(!run(&acts, 12, "text", json!("ab")), "a read-only field");
        assert!(run(&acts, 13, "value", json!(99)));
        assert!(!run(&acts, 13, "value", json!("5")));
        assert!(run(&acts, 14, "invoke", Value::Null));
        assert!(!run(&acts, 15, "invoke", Value::Null), "a disabled menu row");
        assert!(run(&acts, 16, "toggle", Value::Null));
        assert!(open.get(), "the expander flipped");
        assert!(!run(&acts, 99, "invoke", Value::Null), "a stale id");
        assert_eq!(
            *seen.lock().unwrap(),
            vec![
                Input::Invoke,
                Input::Toggle(true),
                Input::Toggle(false),
                Input::Toggle(true),
                Input::Select(2),
                Input::Select(-1),
                Input::Text("ab".into()),
                Input::Value(10.0),
                Input::Toggle(true),
            ]
        );
    }

    /// The control's own event fires only on a change: the same row, the same words or the same value answer true and run nothing.
    #[test]
    fn no_event_without_a_change() {
        let (on, seen) = recorder();
        let mut acts = Acts::default();
        acts.insert(1, Target::Combo { enabled: true, index: 1, count: 3, handler: Some(on.clone()) });
        acts.insert(2, Target::Field { enabled: true, readonly: false, text: "same".into(), handler: Some(on.clone()) });
        acts.insert(3, Target::Slider { enabled: true, value: 10.0, min: 0.0, max: 10.0, handler: Some(on) });
        assert!(run(&acts, 1, "select", json!(1)));
        assert!(run(&acts, 2, "text", json!("same")));
        assert!(run(&acts, 3, "value", json!(50)), "held to 10, the value it has");
        assert!(seen.lock().unwrap().is_empty());
    }
}
