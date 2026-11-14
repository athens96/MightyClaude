//! What the terminal checks share: a window that records what panes ask of it, and waiting for a condition.
#![allow(dead_code)]

use std::collections::{BTreeMap, HashMap};
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use mighty_core::locale::Locale;
use mighty_platform::fake::{FakePtyHost, FakeShellEnvironment, MemoryClipboard};
use mighty_terminal::{AgentTerminal, PaneEvents, PaneKind, PaneOptions, Ports, TerminalFont, TerminalMessage, TerminalPane};

/// What a pane asked of its window, in order.
#[derive(Default)]
pub struct Window {
    pub posts: Mutex<Vec<TerminalMessage>>,
    pub statuses: Mutex<Vec<&'static str>>,
    pub status: Mutex<String>,
    pub errors: Mutex<Vec<String>>,
    pub pressed: Mutex<Vec<String>>,
    pub agents: Mutex<HashMap<String, Arc<AgentTerminal>>>,
    pub light: Mutex<bool>,
    pub font: Mutex<TerminalFont>,
}

impl Window {
    pub fn posts(&self) -> Vec<TerminalMessage> {
        self.posts.lock().unwrap().clone()
    }
    pub fn statuses(&self) -> Vec<&'static str> {
        self.statuses.lock().unwrap().clone()
    }
    pub fn errors(&self) -> Vec<String> {
        self.errors.lock().unwrap().clone()
    }
    /// Every output chunk posted so far, decoded and joined.
    pub fn output(&self) -> String {
        use base64::Engine;
        self.posts()
            .iter()
            .filter_map(|message| match message {
                TerminalMessage::Output { data } => base64::engine::general_purpose::STANDARD.decode(data).ok(),
                _ => None,
            })
            .map(|bytes| String::from_utf8_lossy(&bytes).into_owned())
            .collect()
    }
}

impl PaneEvents for Window {
    fn post(&self, _pane: &str, message: &TerminalMessage) {
        self.posts.lock().unwrap().push(message.clone());
    }
    /// As the service's `SetShellStatus` would: the pane takes the status.
    fn status(&self, _pane: &str, status: &'static str) {
        self.statuses.lock().unwrap().push(status);
        *self.status.lock().unwrap() = status.to_string();
    }
    /// As the service's update would: decided from the status the pane has now.
    fn activity(&self, _pane: &str, busy: bool) {
        let mut current = self.status.lock().unwrap();
        if let Some(next) = mighty_terminal::activity::next_status(&current, Some(busy)) {
            self.statuses.lock().unwrap().push(next);
            *current = next.to_string();
        }
    }
    fn error(&self, text: String) {
        self.errors.lock().unwrap().push(text);
    }
    fn pressed(&self, pane: &str) {
        self.pressed.lock().unwrap().push(pane.to_string());
    }
    fn light(&self) -> bool {
        *self.light.lock().unwrap()
    }
    fn font(&self) -> TerminalFont {
        self.font.lock().unwrap().clone()
    }
    fn agent_terminal(&self, owner: &str) -> Option<Arc<AgentTerminal>> {
        self.agents.lock().unwrap().get(owner).cloned()
    }
}

pub struct Fixture {
    pub ptys: Arc<FakePtyHost>,
    pub clipboard: Arc<MemoryClipboard>,
    pub environment: Arc<FakeShellEnvironment>,
    pub window: Arc<Window>,
    pub folder: PathBuf,
}

impl Fixture {
    pub fn new(name: &str) -> Fixture {
        let folder = std::env::temp_dir().join(format!("mighty-terminal-{name}-{}", std::process::id()));
        std::fs::create_dir_all(&folder).unwrap();
        Fixture {
            ptys: Arc::new(FakePtyHost::default()),
            clipboard: Arc::new(MemoryClipboard::default()),
            environment: Arc::new(FakeShellEnvironment::new(BTreeMap::from([("PATH".to_string(), "/fake/bin".to_string())]))),
            window: Arc::new(Window::default()),
            folder,
        }
    }

    pub fn ports(&self) -> Ports {
        Ports {
            ptys: self.ptys.clone(),
            environment: self.environment.clone(),
            clipboard: self.clipboard.clone(),
            locale: Locale::new("en"),
            engine: "ConPTY".into(),
        }
    }

    pub fn pane(&self, kind: PaneKind, folder: Option<PathBuf>) -> Arc<TerminalPane> {
        let options = PaneOptions { id: "pane".into(), kind, folder, enabled: true, no_profile: true };
        TerminalPane::new(options, self.ports(), self.window.clone())
    }

    pub fn agent(&self) -> Arc<AgentTerminal> {
        AgentTerminal::new(self.folder.clone(), self.ptys.clone(), self.environment.clone())
    }
}

/// Waits for `done`, ticking `tick` meanwhile, up to `ms`.
pub async fn until(mut done: impl FnMut() -> bool, ms: u64, what: &str) {
    let end = Instant::now() + Duration::from_millis(ms);
    while Instant::now() < end {
        if done() {
            return;
        }
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    assert!(done(), "timed out waiting for {what}");
}
