//! A terminal pane's lifecycle (C# `PaneView`'s terminal half, MainWindow.Terminal.cs, and its web-shell bridge,
//! MainWindow.WebShell.Panes.cs): the page draws the terminal (xterm.js) and talks with `terminalMessage`; the host keeps the shell
//! (a pseudo terminal through [`PtyHost`], or an agent's terminal for an `agent-terminal` pane), the output waiting for the page
//! (1 Mi UTF-16 units at most, sent each 32 ms as base64 UTF-8), the exit, the restart, the shell's activity and the footer the page
//! draws under it. Everything the page sees leaves through [`PaneEvents::post`] as `{kind:'terminal', paneId, message}`.
//!
//! Locks: a pane never calls [`PaneEvents`] while it holds its own lock, so the host may call into a pane while it holds its own.

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, MutexGuard, Weak};
use std::time::Duration;

use base64::Engine;
use mighty_core::locale::Locale;
use mighty_core::wire::utf16_len;
use mighty_platform::{Clipboard, PtyControl, PtyHost, PtyRequest, PtySize, ShellEnvironment};
use serde::Serialize;
use serde_json::{Value, json};
use tokio::sync::{mpsc, watch};

use crate::activity::next_status;
use crate::agent::AgentTerminal;
use crate::text::{BoundedText, Utf8Decoder, last_units};

/// The largest `terminalMessage` taken, in UTF-16 units of its JSON (C# `message.Length > 1_048_576`).
pub const MESSAGE_LIMIT: usize = 1_048_576;
/// The output kept while the page cannot take it, in UTF-16 units (C# `terminalPending`); the front goes first.
pub const PENDING_LIMIT: usize = 1_048_576;
/// The longest paste, in UTF-16 units (`terminal.pasteTooLarge` past it).
pub const PASTE_LIMIT: usize = 65_536;
/// The longest selection copied, in UTF-16 units.
pub const COPY_LIMIT: usize = 1_000_000;
/// How often the output waiting goes to the page (C# `terminalFlush`).
pub const FLUSH_INTERVAL: Duration = Duration::from_millis(32);
/// How often an attached shell's activity is read (C# `shellActivity`, the Mac's interval, M/LocalTerminalSession.swift:211-216).
pub const ACTIVITY_INTERVAL: Duration = Duration::from_millis(500);
/// The end of the output the smoke's diagnostics keep.
const TAIL: usize = 400;

/// What the footer and the notice above it say (page `src/panes/terminal/types.ts` `TerminalFooter`; C# `WebTerminalFooter`).
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TerminalFooter {
    /// False: this pane has no terminal here (a remote workspace's shell runs on its host through the composer).
    pub available: bool,
    pub shell: bool,
    /// The notice ("starting", the exit code, a failure); absent while the terminal runs.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub notice: Option<String>,
    pub restart: bool,
    pub restart_enabled: bool,
    /// "ConPTY" (the platform's engine), or the agent terminal's name.
    pub engine: String,
    pub title: String,
    pub directory: String,
    pub grid: String,
}

/// host → page, the `terminal` push's `message` (page `TerminalMessage`).
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum TerminalMessage {
    /// Output as base64 UTF-8: a chunk that ends inside a character is still written whole by xterm.
    Output {
        data: String,
    },
    Paste {
        text: String,
    },
    Focus,
    Reset,
    Font {
        size: f64,
        #[serde(skip_serializing_if = "Option::is_none")]
        family: Option<String>,
    },
    Theme {
        light: bool,
    },
    Footer {
        footer: TerminalFooter,
    },
}

/// The terminal's size and face from the text setting (C# `AppText.Current.TerminalSize` and `CodeFamily`): 12 pt at scale 1, the
/// code font when one is chosen.
#[derive(Debug, Clone, PartialEq)]
pub struct TerminalFont {
    pub size: f64,
    pub family: Option<String>,
}

impl Default for TerminalFont {
    fn default() -> TerminalFont {
        TerminalFont { size: 12.0, family: None }
    }
}

/// What kind of terminal a pane has.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PaneKind {
    /// A shell pane (`shell`): its own interactive shell.
    Shell,
    /// An agent's terminal pane (`agent-terminal`): the commands of its owner's [`AgentTerminal`].
    Agent { owner: Option<String> },
}

/// How a pane is made.
#[derive(Debug, Clone)]
pub struct PaneOptions {
    pub id: String,
    pub kind: PaneKind,
    /// The workspace's folder on this computer; None for a remote workspace, whose shell is its host's.
    pub folder: Option<PathBuf>,
    /// False when this pane may have no terminal now (C#'s smoke outside its terminal step).
    pub enabled: bool,
    /// The shell starts without the user's profile (the smoke).
    pub no_profile: bool,
}

/// The ports and words a pane works with.
#[derive(Clone)]
pub struct Ports {
    pub ptys: Arc<dyn PtyHost>,
    pub environment: Arc<dyn ShellEnvironment>,
    pub clipboard: Arc<dyn Clipboard>,
    pub locale: Locale,
    /// The shell pane's engine as its footer names it ("ConPTY"; the Mac's "Ghostty").
    pub engine: String,
}

/// What a pane asks of the window around it.
pub trait PaneEvents: Send + Sync {
    /// A `{kind:'terminal', paneId, message}` push.
    fn post(&self, pane: &str, message: &TerminalMessage);
    /// The shell pane's status from its terminal (C# `ShellStatusChanged`: idle, running, completed, error).
    fn status(&self, pane: &str, status: &'static str);
    /// The pane's status as the state has it now (C# `service.SessionStatus`).
    fn session_status(&self, pane: &str) -> Option<String>;
    /// The window's error line (C# `owner.error.Text`).
    fn error(&self, text: String);
    /// A press inside the terminal, which never reaches the pane as a pointer event (C# `ActivatePane`).
    fn pressed(&self, pane: &str);
    /// The app's theme is light.
    fn light(&self) -> bool;
    fn font(&self) -> TerminalFont;
    /// The agent terminal of an agent pane (C# `agentTerminals[owner]`).
    fn agent_terminal(&self, owner: &str) -> Option<Arc<AgentTerminal>>;
}

/// A started shell: its control, which start it was, and whether its output has all been read.
#[derive(Clone)]
struct Running {
    generation: u64,
    control: Arc<dyn PtyControl>,
    drained: watch::Receiver<bool>,
    exited: bool,
}

struct State {
    closed: bool,
    ready: bool,
    restarting: bool,
    running: Option<Running>,
    generation: u64,
    flushing: bool,
    /// The page asked for the footer (`terminalOpen`): from now on it is pushed whenever it changes.
    opened: bool,
    footer_sent: Option<TerminalFooter>,
    notice: String,
    notice_visible: bool,
    restart_visible: bool,
    restart_enabled: bool,
    title: String,
    directory: String,
    grid: String,
    columns: i32,
    rows: i32,
    agent: Option<(Arc<AgentTerminal>, u64)>,
    inputs: u64,
    posts: u64,
    tail: String,
}

pub struct TerminalPane {
    options: PaneOptions,
    ports: Ports,
    events: Arc<dyn PaneEvents>,
    state: Mutex<State>,
    pending: Mutex<BoundedText>,
    weak: Weak<TerminalPane>,
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// C# `GetProperty(name).GetInt32()`: a JSON integer within 32 bits, else the failure the message's handling reports.
fn integer(value: &Value, name: &str) -> Result<i32, String> {
    value
        .get(name)
        .and_then(Value::as_i64)
        .and_then(|n| i32::try_from(n).ok())
        .ok_or_else(|| format!("The terminal message has no whole number '{name}'."))
}

/// C#'s check of an OSC 7 working directory: an absolute `file:` address of this computer (no host, or localhost), never a share;
/// its local path.
pub fn directory_from_url(location: &str) -> Option<String> {
    if utf16_len(location) > 8192 {
        return None;
    }
    let url = url::Url::parse(location).ok()?;
    if url.scheme() != "file" {
        return None;
    }
    if url.host_str().is_some_and(|host| !host.is_empty() && !host.eq_ignore_ascii_case("localhost")) {
        return None;
    }
    let path = url.to_file_path().ok()?;
    let text = path.to_string_lossy().into_owned();
    // A share (\\server\share) is no folder of this computer.
    (!text.starts_with("\\\\")).then_some(text)
}

/// The shell's name as the footer gives it (`Path.GetFileNameWithoutExtension(DefaultShell)`).
fn shell_name(shell: &Path) -> String {
    shell.file_stem().map(|stem| stem.to_string_lossy().into_owned()).unwrap_or_default()
}

/// What a pass decided, sent once the pane's lock is let go.
#[derive(Default)]
struct Effects {
    posts: Vec<TerminalMessage>,
    status: Option<&'static str>,
    error: Option<String>,
}

impl TerminalPane {
    pub fn new(options: PaneOptions, ports: Ports, events: Arc<dyn PaneEvents>) -> Arc<TerminalPane> {
        let shell = options.kind == PaneKind::Shell;
        let available = options.enabled && options.folder.is_some();
        let state = State {
            closed: false,
            ready: false,
            restarting: false,
            running: None,
            generation: 0,
            flushing: false,
            opened: false,
            footer_sent: None,
            notice: ports.locale.get("terminal.starting"),
            notice_visible: available,
            restart_visible: available && shell,
            restart_enabled: false,
            title: if available { shell_name(&ports.ptys.default_shell()) } else { String::new() },
            directory: if available { options.folder.as_ref().map(|f| f.display().to_string()).unwrap_or_default() } else { String::new() },
            grid: String::new(),
            columns: 100,
            rows: 30,
            agent: None,
            inputs: 0,
            posts: 0,
            tail: String::new(),
        };
        Arc::new_cyclic(|weak| TerminalPane {
            options,
            ports,
            events,
            state: Mutex::new(state),
            pending: Mutex::new(BoundedText::default()),
            weak: weak.clone(),
        })
    }

    pub fn id(&self) -> &str {
        &self.options.id
    }

    pub fn kind(&self) -> &PaneKind {
        &self.options.kind
    }

    /// The pane has a terminal here (C#: `terminalHost is not null`).
    pub fn available(&self) -> bool {
        self.options.enabled && self.options.folder.is_some()
    }

    fn shell(&self) -> bool {
        self.options.kind == PaneKind::Shell
    }

    fn footer_of(&self, state: &State) -> TerminalFooter {
        let shell = self.shell();
        let available = self.available();
        TerminalFooter {
            available,
            shell,
            notice: (available && state.notice_visible).then(|| state.notice.clone()),
            restart: available && state.restart_visible,
            restart_enabled: available && state.restart_enabled,
            engine: if shell { self.ports.engine.clone() } else { self.ports.locale.get("agentTerminal.terminalPane.title") },
            title: state.title.clone(),
            directory: state.directory.clone(),
            grid: state.grid.clone(),
        }
    }

    pub fn footer(&self) -> TerminalFooter {
        self.footer_of(&lock(&self.state))
    }

    /// `terminalOpen`: the footer the page draws, and from now on a push whenever it changes.
    pub fn open(&self) -> TerminalFooter {
        let mut state = lock(&self.state);
        state.opened = true;
        let footer = self.footer_of(&state);
        state.footer_sent = Some(footer.clone());
        footer
    }

    fn emit(&self, effects: Effects) {
        for message in &effects.posts {
            self.events.post(&self.options.id, message);
        }
        if let Some(status) = effects.status {
            self.events.status(&self.options.id, status);
        }
        if let Some(error) = effects.error {
            self.events.error(error);
        }
    }

    /// C# `PostTerminal`: only to a page whose terminal said it is ready, never once closed.
    fn post(state: &State, effects: &mut Effects, message: TerminalMessage) {
        if !state.closed && state.ready {
            effects.posts.push(message);
        }
    }

    /// C# `RefreshTerminalTheme`: the terminal's colours, and its size and face from the text setting (`look`, read from the window
    /// before the pane's lock is taken).
    fn theme_messages(state: &State, effects: &mut Effects, look: (bool, TerminalFont)) {
        let (light, font) = look;
        TerminalPane::post(state, effects, TerminalMessage::Theme { light });
        TerminalPane::post(state, effects, TerminalMessage::Font { size: font.size, family: font.family });
    }

    fn look(&self) -> (bool, TerminalFont) {
        (self.events.light(), self.events.font())
    }

    fn update_grid(state: &mut State, columns: i32, rows: i32) {
        state.columns = columns.clamp(2, 1000);
        state.rows = rows.clamp(1, 1000);
        state.grid = format!("{}×{}", state.columns, state.rows);
    }

    /// C#'s catch around a message: the error line, and while no shell runs the notice says why it could not start.
    fn failed(&self, error: String) {
        let status = {
            let mut state = lock(&self.state);
            if state.closed {
                return;
            }
            if state.running.is_none() {
                state.notice = format!("{}\n{error}", self.ports.locale.get("terminal.failed"));
                state.restart_enabled = true;
                Some("error")
            } else {
                None
            }
        };
        self.emit(Effects { posts: vec![], status, error: Some(error) });
    }

    /// One `terminalMessage` from the page's terminal.
    pub async fn handle_message(&self, message: &Value) {
        if !self.available() || lock(&self.state).closed {
            return;
        }
        if utf16_len(&message.to_string()) > MESSAGE_LIMIT {
            return;
        }
        if let Err(error) = self.handle(message).await {
            self.failed(error);
        }
    }

    async fn handle(&self, value: &Value) -> Result<(), String> {
        let text = |name: &str| value.get(name).and_then(Value::as_str);
        match text("type").unwrap_or("") {
            "ready" => {
                let first = {
                    let mut state = lock(&self.state);
                    let first = !state.ready;
                    state.ready = true;
                    first
                };
                if !first {
                    // A page loaded again: its new terminal takes the colours and the font again.
                    let look = self.look();
                    let mut effects = Effects::default();
                    TerminalPane::theme_messages(&lock(&self.state), &mut effects, look);
                    self.emit(effects);
                    return Ok(());
                }
                let (columns, rows) = (integer(value, "columns")?, integer(value, "rows")?);
                TerminalPane::update_grid(&mut lock(&self.state), columns, rows);
                match &self.options.kind {
                    PaneKind::Agent { owner } => {
                        if !self.connect_agent(owner.as_deref(), columns, rows)? {
                            return Err("Agent terminal is no longer available.".into());
                        }
                        let look = self.look();
                        let mut effects = Effects::default();
                        {
                            let mut state = lock(&self.state);
                            state.notice_visible = false;
                            state.flushing = true;
                            TerminalPane::theme_messages(&state, &mut effects, look);
                        }
                        self.emit(effects);
                        Ok(())
                    }
                    PaneKind::Shell => self.start_shell().await,
                }
            }
            "input" => {
                let agent = {
                    let mut state = lock(&self.state);
                    state.inputs += 1;
                    state.agent.as_ref().map(|(agent, _)| agent.clone())
                };
                if let Some(input) = text("text") {
                    let taken = match agent {
                        Some(agent) => Some(agent.write(input)),
                        None => self.control().map(|control| control.write(input.as_bytes())),
                    };
                    if taken == Some(false) {
                        self.events.error(self.ports.locale.get("terminal.inputBusy"));
                    }
                }
                Ok(())
            }
            "resize" => {
                let (columns, rows) = (integer(value, "columns")?, integer(value, "rows")?);
                let agent = {
                    let mut state = lock(&self.state);
                    TerminalPane::update_grid(&mut state, columns, rows);
                    state.agent.as_ref().map(|(agent, _)| agent.clone())
                };
                match agent {
                    Some(agent) => agent.resize(columns, rows),
                    None => match self.control() {
                        Some(control) => control
                            .resize(PtySize { columns: columns.clamp(2, 1000) as u16, rows: rows.clamp(1, 1000) as u16 })
                            .map_err(|error| error.to_string()),
                        None => Ok(()),
                    },
                }
            }
            "title" => {
                if let Some(title) = text("title").filter(|t| (1..=200).contains(&utf16_len(t)) && !t.chars().any(char::is_control)) {
                    lock(&self.state).title = title.to_string();
                }
                Ok(())
            }
            "directory" => {
                if let Some(path) = text("url").and_then(directory_from_url) {
                    lock(&self.state).directory = path;
                }
                Ok(())
            }
            "pressed" => {
                self.events.pressed(&self.options.id);
                Ok(())
            }
            "copy" => match text("text").filter(|t| utf16_len(t) <= COPY_LIMIT) {
                Some(selection) => self.ports.clipboard.write_text(selection).map_err(|error| error.to_string()),
                None => Ok(()),
            },
            "paste" => {
                let Some(pasted) = self.ports.clipboard.read_text().map_err(|error| error.to_string())? else { return Ok(()) };
                let mut effects = Effects::default();
                {
                    let state = lock(&self.state);
                    if state.closed {
                        return Ok(());
                    }
                    if utf16_len(&pasted) <= PASTE_LIMIT {
                        TerminalPane::post(&state, &mut effects, TerminalMessage::Paste { text: pasted });
                    } else {
                        effects.error = Some(self.ports.locale.get("terminal.pasteTooLarge"));
                    }
                }
                self.emit(effects);
                Ok(())
            }
            _ => Ok(()),
        }
    }

    /// The running shell's control, while it has one (exited or not).
    fn control(&self) -> Option<Arc<dyn PtyControl>> {
        lock(&self.state).running.as_ref().map(|running| running.control.clone())
    }

    /// The shell this shell pane has attached and running, the one the window's activity reading asks about; None for any other pane
    /// (C# `AttachedShell`).
    fn attached(&self) -> Option<Arc<dyn PtyControl>> {
        let state = lock(&self.state);
        if !self.shell() || state.closed {
            return None;
        }
        state.running.as_ref().filter(|running| !running.exited).map(|running| running.control.clone())
    }

    /// One reading of the shell's activity (C# `ShellProcessProbe.IsBusy` then `ApplyShellActivity`): the pane is running while a
    /// command occupies its terminal and idle at its prompt; a pane that has exited or failed keeps that, and a reading of a shell the
    /// pane no longer has changes nothing. Whether a shell was read.
    pub fn read_activity(&self) -> bool {
        let Some(control) = self.attached() else { return false };
        let busy = control.busy();
        let still = self.attached().is_some_and(|current| Arc::ptr_eq(&current, &control));
        if !still {
            return true;
        }
        if let Some(next) = self.events.session_status(&self.options.id).and_then(|current| next_status(&current, busy)) {
            self.events.status(&self.options.id, next);
        }
        true
    }

    /// C# `ConnectAgentTerminal`: an agent pane's terminal shows its owner's commands, what they printed so far first.
    fn connect_agent(&self, owner: Option<&str>, columns: i32, rows: i32) -> Result<bool, String> {
        let Some(source) = owner.and_then(|owner| self.events.agent_terminal(owner)) else { return Ok(false) };
        let weak = self.weak.clone();
        let id = source.subscribe(Arc::new(move |text: &str| {
            if let Some(pane) = weak.upgrade() {
                pane.receive(text);
            }
        }));
        lock(&self.state).agent = Some((source.clone(), id));
        self.receive(&source.replay());
        source.resize(columns, rows)?;
        Ok(true)
    }

    /// C# `ReceiveTerminalOutput`: output kept for the next flush, its last [`PENDING_LIMIT`] while the page does not take it.
    fn receive(&self, text: &str) {
        lock(&self.pending).push(text, PENDING_LIMIT);
    }

    /// C# `StartTerminalShellAsync`: the platform's default shell in the workspace's folder, at the page's grid; once it runs the
    /// notice goes, the output flows, and the pane is idle until a reading says a command runs.
    async fn start_shell(&self) -> Result<(), String> {
        let folder = self.options.folder.clone().ok_or("A local workspace directory is required.")?;
        let shell = self.ports.ptys.default_shell();
        let size = {
            let state = lock(&self.state);
            PtySize { columns: state.columns as u16, rows: state.rows as u16 }
        };
        let request = PtyRequest {
            args: self.ports.ptys.shell_arguments(&shell, None, self.options.no_profile),
            program: shell,
            cwd: folder,
            env: self.ports.environment.current().values,
            size,
        };
        let session = self.ports.ptys.open(request).await.map_err(|error| error.to_string())?;
        let control = session.control.clone();
        let (drained_sender, drained) = watch::channel(false);
        let look = self.look();
        let mut effects = Effects::default();
        let generation = {
            let mut state = lock(&self.state);
            if state.closed {
                drop(state);
                tokio::spawn(async move {
                    let _ = control.close().await;
                });
                return Ok(());
            }
            state.generation += 1;
            let generation = state.generation;
            state.running = Some(Running { generation, control: control.clone(), drained: drained.clone(), exited: false });
            state.notice_visible = false;
            state.restart_visible = false;
            state.flushing = true;
            TerminalPane::theme_messages(&state, &mut effects, look);
            generation
        };
        tokio::spawn(TerminalPane::pump(self.weak.clone(), session.output, drained_sender));
        tokio::spawn(TerminalPane::observe_exit(self.weak.clone(), generation, control, drained));
        // Attached: idle until a reading says a command runs (M/LocalTerminalSession.swift:144).
        effects.status = Some("idle");
        self.emit(effects);
        Ok(())
    }

    async fn pump(pane: Weak<TerminalPane>, mut output: mpsc::Receiver<Vec<u8>>, drained: watch::Sender<bool>) {
        let mut decoder = Utf8Decoder::default();
        while let Some(bytes) = output.recv().await {
            let text = decoder.decode(&bytes);
            if let Some(pane) = pane.upgrade()
                && !text.is_empty()
            {
                pane.receive(&text);
            }
        }
        let rest = decoder.finish();
        if let Some(pane) = pane.upgrade()
            && !rest.is_empty()
        {
            pane.receive(&rest);
        }
        drained.send_replace(true);
    }

    /// C# `ObserveTerminalExit`: the shell's exit waited on, its terminal closed and its last output read; then, unless the pane
    /// closed or restarted meanwhile, that output sent, the pane completed (or error when the exit could not be read), and the
    /// notice says so with the restart button.
    async fn observe_exit(pane: Weak<TerminalPane>, generation: u64, control: Arc<dyn PtyControl>, mut drained: watch::Receiver<bool>) {
        let code = control.wait().await;
        let closed = control.close().await;
        let _ = drained.wait_for(|done| *done).await;
        let Some(pane) = pane.upgrade() else { return };
        let failure = match (&code, &closed) {
            (Err(error), _) | (Ok(_), Err(error)) => Some(error.to_string()),
            _ => None,
        };
        {
            let mut state = lock(&pane.state);
            if state.closed || state.running.as_ref().is_none_or(|running| running.generation != generation) {
                return;
            }
            if let Some(running) = state.running.as_mut() {
                running.exited = true;
            }
        }
        pane.flush();
        let effects = {
            let mut state = lock(&pane.state);
            state.flushing = false;
            state.notice = match (&failure, &code) {
                (None, Ok(code)) => pane.ports.locale.get_with("terminal.exited", &[("code", &code.to_string())]),
                _ => format!("{}\n{}", pane.ports.locale.get("terminal.failed"), failure.unwrap_or_default()),
            };
            state.notice_visible = true;
            state.restart_visible = true;
            state.restart_enabled = true;
            let failed = code.is_err() || closed.is_err();
            Effects { posts: vec![], status: Some(if failed { "error" } else { "completed" }), error: None }
        };
        pane.emit(effects);
    }

    /// `terminalRestart` (C# `RestartTerminalAsync`): the shell pane's shell ended and started again in the same terminal (reset);
    /// one restart at a time.
    pub async fn restart(&self) {
        let previous = {
            let mut state = lock(&self.state);
            if state.closed || state.restarting || !self.shell() || !self.available() {
                return;
            }
            state.restarting = true;
            state.restart_enabled = false;
            state.running.take()
        };
        self.events.status(&self.options.id, "idle");
        if let Some(previous) = previous {
            let _ = previous.control.close().await;
            let mut drained = previous.drained;
            let _ = drained.wait_for(|done| *done).await;
        }
        let ready = {
            let mut state = lock(&self.state);
            if state.closed {
                state.restarting = false;
                return;
            }
            lock(&self.pending).clear();
            state.notice = self.ports.locale.get("terminal.starting");
            state.notice_visible = true;
            state.ready
        };
        // A page whose terminal never said it was ready starts the shell with its ready.
        if ready {
            let mut effects = Effects::default();
            TerminalPane::post(&lock(&self.state), &mut effects, TerminalMessage::Reset);
            self.emit(effects);
            if let Err(error) = self.start_shell().await {
                let effects = {
                    let mut state = lock(&self.state);
                    if state.closed {
                        Effects::default()
                    } else {
                        state.notice = format!("{}\n{error}", self.ports.locale.get("terminal.failed"));
                        state.restart_visible = true;
                        state.restart_enabled = true;
                        Effects { posts: vec![], status: Some("error"), error: None }
                    }
                };
                self.emit(effects);
            }
        }
        lock(&self.state).restarting = false;
    }

    /// C# `FlushTerminal`: the output waiting, to a ready page as one base64 UTF-8 chunk.
    pub fn flush(&self) {
        let mut effects = Effects::default();
        {
            let mut state = lock(&self.state);
            if state.closed || !state.ready {
                return;
            }
            let text = lock(&self.pending).take();
            if text.is_empty() {
                return;
            }
            state.posts += 1;
            let tail = format!("{}{text}", state.tail);
            state.tail = last_units(&tail, TAIL).to_string();
            let data = base64::engine::general_purpose::STANDARD.encode(text.as_bytes());
            TerminalPane::post(&state, &mut effects, TerminalMessage::Output { data });
        }
        self.emit(effects);
    }

    /// C# `RefreshTerminalTheme` when the theme or the text setting changed: the colours and the font again, to a ready page.
    pub fn restyle(&self) {
        let look = self.look();
        let mut effects = Effects::default();
        {
            let state = lock(&self.state);
            if state.closed || !state.ready {
                return;
            }
            TerminalPane::theme_messages(&state, &mut effects, look);
        }
        self.emit(effects);
    }

    /// One tick of the window's 32 ms clock: the output while the terminal runs, and the footer when it changed since the page last
    /// had it (C# `QueueWebTerminalFooter`, after `terminalOpen`).
    pub fn tick(&self) {
        if lock(&self.state).flushing {
            self.flush();
        }
        let footer = {
            let mut state = lock(&self.state);
            if !state.opened || state.closed {
                return;
            }
            let footer = self.footer_of(&state);
            if state.footer_sent.as_ref() == Some(&footer) {
                return;
            }
            state.footer_sent = Some(footer.clone());
            footer
        };
        self.events.post(&self.options.id, &TerminalMessage::Footer { footer });
    }

    /// The page will draw everything again (it loaded again): the footer goes out with its next open.
    pub fn forget_sent(&self) {
        let mut state = lock(&self.state);
        state.footer_sent = None;
        state.opened = false;
    }

    /// Text the app puts into the running shell for the user (C# `TryWrite` under `TerminalInputPolicy`): None without a shell.
    pub fn write(&self, text: &str) -> Option<bool> {
        self.control().map(|control| control.write(text.as_bytes()))
    }

    /// Whether the pane's shell started and has neither exited nor been closed with the pane.
    pub fn is_running(&self) -> bool {
        let state = lock(&self.state);
        !state.closed && state.running.as_ref().is_some_and(|running| !running.exited)
    }

    pub fn is_closed(&self) -> bool {
        lock(&self.state).closed
    }

    /// For the smokes: whether the view said it is ready, output waiting, output chunks sent on, the shell running.
    pub fn diagnostics(&self) -> Value {
        let pending = lock(&self.pending).units();
        let state = lock(&self.state);
        json!({
            "ready": state.ready, "closed": state.closed, "pending": pending, "posts": state.posts, "flushing": state.flushing,
            "inputs": state.inputs, "tail": state.tail, "running": state.running.as_ref().is_some_and(|r| !r.exited),
        })
    }

    /// C# `DisposeTerminalAsync`: the pane takes nothing more; an agent's terminal is let go (it keeps running), a shell's terminal is
    /// closed (its processes ended) and its last output read. Calling it again waits for the same close.
    pub async fn dispose(&self) {
        let (agent, running) = {
            let mut state = lock(&self.state);
            state.closed = true;
            state.flushing = false;
            (state.agent.take(), state.running.clone())
        };
        if let Some((agent, id)) = agent {
            agent.unsubscribe(id);
        }
        if let Some(running) = running {
            let _ = running.control.close().await;
            let mut drained = running.drained;
            let _ = drained.wait_for(|done| *done).await;
        }
    }
}
