//! The page's host (C# `WebBridge`, MightyCode.WinUI/WebBridge.cs): page → host messages arrive through one command, `host_post`;
//! every host → page message — a call's result and every push — leaves through one Tauri event, `host-message`, from one thread,
//! so they reach the page in the order they were posted (a push posted while a call is handled arrives before its result).
//!
//! - A call `{kind:"call", id, method, args?}` is answered `{kind:"result", id, ok, value?, error?}` (a null value or error left
//!   out). Calls are handled one at a time, in order. A method this host does not have yet answers
//!   `ok:false, error:"unimplemented: <method>"`; its name is logged once and kept for `tauri-logs/unimplemented.json`.
//! - Pushes are dropped until the page has called `ready`, which pushes the whole snapshot before its result; a page that loads
//!   again starts over. After every call the host flushes: a `delta` when the state changed since the last one sent, a `shell`
//!   when the frame model did.
//!
//! The state lives in memory only: the skeleton never writes `workspace-state.json`.

use std::collections::{BTreeSet, HashMap};
use std::future::Future;
use std::path::PathBuf;
use std::pin::Pin;
use std::sync::{Arc, Mutex, MutexGuard, mpsc};

use mighty_core::AppSnapshot;
use mighty_core::layout::Node;
use mighty_core::locale::Locale;
use mighty_core::shell::{ShellContext, ShellModel, shell_model};
use serde_json::{Map, Value, json};
use tauri::{AppHandle, Emitter};

use crate::logfile::LogFile;
use crate::smoke::Smoke;

/// The event every host → page message travels on, and the window it goes to.
pub const EVENT: &str = "host-message";
pub const WINDOW: &str = "main";

pub type Reply = Result<Value, String>;
type Pending = Pin<Box<dyn Future<Output = Reply> + Send>>;
type Handler = Box<dyn Fn(Arc<Host>, Value) -> Pending + Send + Sync>;

/// The methods the page may call, by name.
#[derive(Default)]
pub struct Registry {
    handlers: HashMap<&'static str, Handler>,
}

impl Registry {
    pub fn add<F, Fut>(&mut self, name: &'static str, handler: F)
    where
        F: Fn(Arc<Host>, Value) -> Fut + Send + Sync + 'static,
        Fut: Future<Output = Reply> + Send + 'static,
    {
        self.handlers.insert(name, Box::new(move |host, args| Box::pin(handler(host, args))));
    }

    pub fn names(&self) -> BTreeSet<&'static str> {
        self.handlers.keys().copied().collect()
    }
}

/// The state the page sees and what was last sent of it.
pub struct View {
    pub snapshot: AppSnapshot,
    /// Bumped by every change; a flush with nothing new sends nothing.
    revision: u64,
    sent_revision: Option<u64>,
    pub ready: bool,
    sent_state: Option<String>,
    sent_sessions: HashMap<String, Value>,
    sent_order: Vec<String>,
    sent_shell: Option<String>,
    /// The window's `layoutDefaults`: layouts made for workspaces without a saved one, kept so their ids stay put.
    pub layout_defaults: HashMap<String, Node>,
}

impl View {
    /// Changes the in-memory state (never the file).
    pub fn update(&mut self, change: impl FnOnce(&mut AppSnapshot)) {
        change(&mut self.snapshot);
        self.revision += 1;
    }
}

pub struct Host {
    pub app: AppHandle,
    pub data_dir: PathBuf,
    pub locale: Locale,
    pub version: String,
    /// The load's notice or failure, shown in the frame's error banner.
    pub banner: Option<String>,
    pub log: LogFile,
    pub smoke: Option<Smoke>,
    view: Mutex<View>,
    registry: Registry,
    outbox: Mutex<mpsc::Sender<Value>>,
    unimplemented: Mutex<BTreeSet<String>>,
}

impl Host {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        app: AppHandle,
        data_dir: PathBuf,
        snapshot: AppSnapshot,
        locale: Locale,
        version: String,
        banner: Option<String>,
        log: LogFile,
        smoke: Option<Smoke>,
        registry: Registry,
    ) -> Arc<Host> {
        let (outbox, outgoing) = mpsc::channel::<Value>();
        let emitter = app.clone();
        // One thread posts every message, in order.
        std::thread::Builder::new()
            .name("host-message".into())
            .spawn(move || {
                for message in outgoing {
                    let _ = emitter.emit_to(WINDOW, EVENT, &message);
                }
            })
            .expect("the host-message thread");
        let view = View {
            snapshot,
            revision: 0,
            sent_revision: None,
            ready: false,
            sent_state: None,
            sent_sessions: HashMap::new(),
            sent_order: vec![],
            sent_shell: None,
            layout_defaults: HashMap::new(),
        };
        Arc::new(Host {
            app,
            data_dir,
            locale,
            version,
            banner,
            log,
            smoke,
            view: Mutex::new(view),
            registry,
            outbox: Mutex::new(outbox),
            unimplemented: Mutex::new(BTreeSet::new()),
        })
    }

    pub fn view(&self) -> MutexGuard<'_, View> {
        self.view.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// Posts a message whatever the page's state (a call's result).
    fn post(&self, message: Value) {
        if let Ok(outbox) = self.outbox.lock() {
            let _ = outbox.send(message);
        }
    }

    /// Posts a push, or drops it when the page has not called `ready` since it loaded.
    fn push(&self, view: &View, message: Value) {
        if !view.ready {
            return;
        }
        if let Some(smoke) = &self.smoke {
            smoke.pushed(message.get("kind").and_then(Value::as_str).unwrap_or(""));
        }
        self.post(message);
    }

    /// The page is loading (again): it gets nothing until it calls `ready`.
    pub fn page_started(&self) {
        let mut view = self.view();
        view.ready = false;
        view.sent_revision = None;
        view.sent_state = None;
        view.sent_sessions.clear();
        view.sent_order.clear();
        view.sent_shell = None;
    }

    /// `ready`: pushes the whole snapshot (before the call's result) and turns pushes on.
    pub fn ready(&self) {
        let mut view = self.view();
        view.ready = true;
        let snapshot = serde_json::to_value(&view.snapshot).unwrap_or(Value::Null);
        let (state, sessions) = split(&snapshot);
        view.sent_state = Some(state.to_string());
        view.sent_order = sessions.iter().map(|(id, _)| id.clone()).collect();
        view.sent_sessions = sessions.into_iter().collect();
        view.sent_revision = Some(view.revision);
        self.push(&view, json!({"kind": "snapshot", "snapshot": snapshot}));
    }

    pub fn shell_model(&self, view: &mut View) -> ShellModel {
        let context = ShellContext { locale: self.locale, version: &self.version, now: chrono::Utc::now(), error: self.banner.clone() };
        let View { snapshot, layout_defaults, .. } = view;
        shell_model(snapshot, &context, layout_defaults)
    }

    /// What changed since the last flush: a `delta` (panes replaced whole, `{id, session}`; removed ids; the order when it
    /// changed; every other field when one of them changed), then the frame model when it changed.
    pub fn flush(&self) {
        let mut view = self.view();
        if !view.ready {
            return;
        }
        if view.sent_revision != Some(view.revision) {
            let snapshot = serde_json::to_value(&view.snapshot).unwrap_or(Value::Null);
            let (state, sessions) = split(&snapshot);
            let state_text = state.to_string();
            let order: Vec<String> = sessions.iter().map(|(id, _)| id.clone()).collect();
            let changed: Vec<Value> = sessions
                .iter()
                .filter(|(id, session)| view.sent_sessions.get(id) != Some(session))
                .map(|(id, session)| json!({"id": id, "session": session}))
                .collect();
            let removed: Vec<&String> = view.sent_order.iter().filter(|id| !order.contains(id)).collect();
            let mut delta = Map::new();
            delta.insert("kind".into(), json!("delta"));
            delta.insert("sessions".into(), json!(changed));
            delta.insert("removed".into(), json!(removed));
            if order != view.sent_order {
                delta.insert("order".into(), json!(order));
            }
            if view.sent_state.as_deref() != Some(state_text.as_str()) {
                delta.insert("state".into(), state);
            }
            if !changed.is_empty() || !removed.is_empty() || delta.contains_key("order") || delta.contains_key("state") {
                self.push(&view, Value::Object(delta));
            }
            view.sent_state = Some(state_text);
            view.sent_sessions = sessions.into_iter().collect();
            view.sent_order = order;
            view.sent_revision = Some(view.revision);
        }
        let model = self.shell_model(&mut view);
        let text = serde_json::to_string(&model).unwrap_or_default();
        if view.sent_shell.as_deref() != Some(text.as_str()) {
            self.push(&view, json!({"kind": "shell", "shell": model}));
            view.sent_shell = Some(text);
        }
    }

    fn unimplemented(&self, method: &str) {
        let Ok(mut names) = self.unimplemented.lock() else { return };
        if names.insert(method.to_string()) {
            self.log.line(&format!("unimplemented: {method}"));
        }
    }

    pub fn unimplemented_methods(&self) -> Vec<String> {
        self.unimplemented.lock().map(|n| n.iter().cloned().collect()).unwrap_or_default()
    }

    /// Adds this run's unimplemented methods to `tauri-logs/unimplemented.json` (the porting checklist, kept across runs).
    pub fn write_unimplemented(&self) {
        let path = self.log.dir().join("unimplemented.json");
        let mut all: BTreeSet<String> = std::fs::read(&path)
            .ok()
            .and_then(|bytes| serde_json::from_slice::<Value>(&bytes).ok())
            .and_then(|v| v.get("methods").and_then(Value::as_array).cloned())
            .map(|list| list.iter().filter_map(|m| m.as_str().map(str::to_string)).collect())
            .unwrap_or_default();
        all.extend(self.unimplemented_methods());
        let body = json!({
            "about": "Page methods the Rust host answered with \"unimplemented\", every run together: the porting checklist.",
            "updatedAt": chrono::Utc::now().to_rfc3339(),
            "implemented": self.registry.names(),
            "methods": all,
        });
        let _ = std::fs::create_dir_all(self.log.dir());
        if let Err(error) = std::fs::write(&path, serde_json::to_vec_pretty(&body).unwrap_or_default()) {
            self.log.line(&format!("cannot write {}: {error}", path.display()));
        }
    }

    /// One page → host message, handled to the end before the next (see the module comment).
    pub async fn dispatch(self: Arc<Self>, message: Value) {
        if message.get("kind").and_then(Value::as_str) != Some("call") {
            return;
        }
        let (Some(id), Some(method)) =
            (message.get("id").filter(|id| id.is_number()).cloned(), message.get("method").and_then(Value::as_str))
        else {
            self.log.line("a call without an id or a method was ignored");
            return;
        };
        let args = message.get("args").cloned().unwrap_or(Value::Null);
        if let Some(smoke) = &self.smoke {
            smoke.called(method);
        }
        let reply = match self.registry.handlers.get(method) {
            Some(handler) => handler(self.clone(), args).await,
            None => {
                self.unimplemented(method);
                Err(format!("unimplemented: {method}"))
            }
        };
        let mut result = Map::new();
        result.insert("kind".into(), json!("result"));
        result.insert("id".into(), id);
        match reply {
            Ok(value) => {
                result.insert("ok".into(), json!(true));
                if !value.is_null() {
                    result.insert("value".into(), value);
                }
            }
            Err(error) => {
                result.insert("ok".into(), json!(false));
                result.insert("error".into(), json!(error));
            }
        }
        self.post(Value::Object(result));
        self.flush();
        if let Some(smoke) = &self.smoke {
            smoke.check(&self);
        }
    }
}

/// A snapshot's JSON as the delta reads it: every field but the panes, and the panes by id in order.
fn split(snapshot: &Value) -> (Value, Vec<(String, Value)>) {
    let mut state = snapshot.as_object().cloned().unwrap_or_default();
    let sessions = match state.remove("sessions") {
        Some(Value::Array(list)) => list.into_iter().map(|s| (s.get("id").and_then(Value::as_str).unwrap_or("").to_string(), s)).collect(),
        _ => vec![],
    };
    (Value::Object(state), sessions)
}
