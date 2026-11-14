//! A pane-scoped terminal for an agent's commands (C# `AgentTerminal`, AgentIO/AgentTerminal.cs): `run_in_terminal`,
//! `read_latest_output` and `stop` of the agent's `mighty-terminal` tools (Agent IO, W7), and a transcript's shell block runs (W4).
//! Each command runs in a terminal of its own (the default shell running it, without the user's profile) in the pane's folder;
//! handles and output never cross agent panes. The agent pane's terminal pane shows what all of them print ([`AgentTerminal::replay`]
//! and the output listeners), and its input goes to the newest command.

use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::Duration;

use mighty_core::wire;
use mighty_platform::{PtyControl, PtyHost, PtyRequest, PtySize, ShellEnvironment};
use serde::Serialize;
use tokio::sync::{mpsc, watch};

use crate::cleaner::TerminalTextCleaner;
use crate::prompt::{PromptRead, PromptTerminal};
use crate::text::{BoundedText, Utf8Decoder};

/// Commands running at once in one pane.
pub const MAXIMUM_RUNNING: usize = 8;
/// Commands one pane remembers (the oldest finished one goes first).
pub const MAXIMUM_COMMANDS: usize = 64;
/// A command's unread output and the pane's screen, in UTF-16 units (C#'s `StringBuilder` lengths).
pub const BUFFER_LIMIT: usize = 1_048_576;
/// A command's own output kept for readers with their own cursor, in UTF-8 bytes.
pub const LOG_LIMIT: usize = 1_048_576;
/// What one read hands the agent, in UTF-16 units (at most 63 KiB of UTF-8 before cleaning).
const READ_LIMIT: usize = 21_000;
/// How long `run` waits for a command before it gives back a handle to poll.
const RUN_WAIT: Duration = Duration::from_secs(12);
/// How long `stop` waits after Ctrl+C before it ends the command's processes.
const STOP_GRACE: Duration = Duration::from_secs(3);

/// What a command has printed and where it stands (C# `AgentTerminalResult`), as the agent's tools answer it.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentTerminalResult {
    pub handle: String,
    /// "running" or "done".
    pub status: &'static str,
    pub output: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub exit_code: Option<i32>,
    /// Output that came faster than it was read was dropped from the front.
    pub output_dropped: bool,
    pub more_remains: bool,
}

/// Why the terminal refused (C#'s exceptions, their words).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AgentTerminalError {
    InvalidCommand,
    TooManyRunning,
    HistoryFull,
    UnknownHandle,
    Closed,
    /// The command's terminal did not start (the platform's reason).
    Start(String),
}

impl std::fmt::Display for AgentTerminalError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            AgentTerminalError::InvalidCommand => f.write_str("Invalid terminal command."),
            AgentTerminalError::TooManyRunning => f.write_str("Too many terminal processes in this pane."),
            AgentTerminalError::HistoryFull => f.write_str("Terminal history is full."),
            AgentTerminalError::UnknownHandle => f.write_str("Unknown handle for this agent pane."),
            AgentTerminalError::Closed => f.write_str("The agent pane's terminal is closed."),
            AgentTerminalError::Start(reason) => write!(f, "{reason}"),
        }
    }
}

impl std::error::Error for AgentTerminalError {}

/// Someone who sees everything the pane's commands print (the agent pane's terminal pane), on the reading task.
pub type OutputListener = Arc<dyn Fn(&str) + Send + Sync>;

/// A command's start: None while it starts, then whether it did (the platform's reason when not).
type Started = Option<Result<(), String>>;

struct Command {
    handle: String,
    control: Option<Arc<dyn PtyControl>>,
    pending: BoundedText,
    cleaner: TerminalTextCleaner,
    dropped: bool,
    finished: bool,
    /// The command's own raw output, its last [`LOG_LIMIT`] bytes, for readers with their own cursor; `log_start` is the stream
    /// position of its first byte.
    log: String,
    log_start: usize,
    exit_code: Option<i32>,
    started: watch::Receiver<Started>,
    completed: watch::Receiver<bool>,
}

struct Gate {
    commands: Vec<Command>,
    screen: BoundedText,
    active: Option<String>,
    closing: bool,
}

impl Gate {
    fn command(&self, handle: &str) -> Option<&Command> {
        self.commands.iter().find(|c| c.handle == handle)
    }
    fn command_mut(&mut self, handle: &str) -> Option<&mut Command> {
        self.commands.iter_mut().find(|c| c.handle == handle)
    }
}

pub struct AgentTerminal {
    directory: PathBuf,
    ptys: Arc<dyn PtyHost>,
    environment: Arc<dyn ShellEnvironment>,
    gate: Mutex<Gate>,
    listeners: Mutex<Vec<(u64, OutputListener)>>,
    next_listener: AtomicU64,
}

async fn wait_started(mut started: watch::Receiver<Started>) -> Result<(), String> {
    match started.wait_for(Option::is_some).await {
        Ok(value) => value.clone().unwrap_or(Ok(())),
        Err(_) => Err("the command's start was lost".into()),
    }
}

async fn wait_completed(mut completed: watch::Receiver<bool>) {
    let _ = completed.wait_for(|done| *done).await;
}

impl AgentTerminal {
    /// A terminal whose commands run in `directory` (the workspace's folder, or a delegated child's worktree), each through `ptys`
    /// with the CLI environment `environment` has now.
    pub fn new(directory: PathBuf, ptys: Arc<dyn PtyHost>, environment: Arc<dyn ShellEnvironment>) -> Arc<AgentTerminal> {
        Arc::new(AgentTerminal {
            directory,
            ptys,
            environment,
            gate: Mutex::new(Gate { commands: vec![], screen: BoundedText::default(), active: None, closing: false }),
            listeners: Mutex::new(vec![]),
            next_listener: AtomicU64::new(1),
        })
    }

    fn lock(&self) -> MutexGuard<'_, Gate> {
        self.gate.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// Everything the pane's commands printed, their echoed commands and exit lines, the last [`BUFFER_LIMIT`] of it.
    pub fn replay(&self) -> String {
        self.lock().screen.as_str().to_string()
    }

    /// Whether any command still runs.
    pub fn is_running(&self) -> bool {
        self.lock().commands.iter().any(|c| !c.finished)
    }

    /// `run_in_terminal`: starts `text` and waits for it up to 12 s; what it printed so far and whether it ended.
    pub async fn run(self: &Arc<Self>, text: &str) -> Result<AgentTerminalResult, AgentTerminalError> {
        let handle = self.begin(text, None).await?;
        let completed = self.lock().command(&handle).map(|c| c.completed.clone());
        if let Some(completed) = completed {
            let _ = tokio::time::timeout(RUN_WAIT, wait_completed(completed)).await;
        }
        self.read(&handle)
    }

    /// Starts `text` as [`AgentTerminal::run`] does, without waiting for it: its handle once the process started. `output` gets the
    /// process's own output as it comes (not the echoed command, nor the exit line), on the reading task. A transcript's shell block
    /// runs this way.
    pub async fn start(self: &Arc<Self>, text: &str, output: Option<OutputListener>) -> Result<String, AgentTerminalError> {
        self.begin(text, output).await
    }

    /// The exit code of a started command once it ended (-1 when it could not be waited on).
    pub async fn exited(&self, handle: &str) -> Result<Option<i32>, AgentTerminalError> {
        let completed = self.lock().command(handle).map(|c| c.completed.clone()).ok_or(AgentTerminalError::UnknownHandle)?;
        wait_completed(completed).await;
        Ok(self.lock().command(handle).and_then(|c| c.exit_code))
    }

    async fn begin(self: &Arc<Self>, text: &str, output: Option<OutputListener>) -> Result<String, AgentTerminalError> {
        if text.trim().is_empty() || text.len() > 65_536 || text.contains('\0') {
            return Err(AgentTerminalError::InvalidCommand);
        }
        let (started_sender, started) = watch::channel::<Started>(None);
        let (completed_sender, completed) = watch::channel(false);
        let handle = wire::new_id();
        {
            let mut gate = self.lock();
            if gate.closing {
                return Err(AgentTerminalError::Closed);
            }
            if gate.commands.iter().filter(|c| !c.finished).count() >= MAXIMUM_RUNNING {
                return Err(AgentTerminalError::TooManyRunning);
            }
            while gate.commands.len() >= MAXIMUM_COMMANDS {
                let Some(oldest) = gate.commands.iter().position(|c| c.finished) else { return Err(AgentTerminalError::HistoryFull) };
                gate.commands.remove(oldest);
            }
            gate.commands.push(Command {
                handle: handle.clone(),
                control: None,
                pending: BoundedText::default(),
                cleaner: TerminalTextCleaner::new(),
                dropped: false,
                finished: false,
                log: String::new(),
                log_start: 0,
                exit_code: None,
                started: started.clone(),
                completed,
            });
            gate.active = Some(handle.clone());
        }
        self.receive(&handle, &format!("\r\n> {text}\r\n"));
        let shell = self.ptys.default_shell();
        let request = PtyRequest {
            args: self.ptys.shell_arguments(&shell, Some(text), true),
            program: shell,
            cwd: self.directory.clone(),
            env: self.environment.current().values,
            size: PtySize::default(),
        };
        tokio::spawn(self.clone().complete(handle.clone(), request, output, started_sender, completed_sender));
        match wait_started(started).await {
            Ok(()) => Ok(handle),
            Err(reason) => {
                // Never a pollable handle left "running" (its exit line follows from the command's own task).
                if let Some(command) = self.lock().command_mut(&handle) {
                    command.finished = true;
                    command.exit_code = Some(-1);
                }
                Err(AgentTerminalError::Start(reason))
            }
        }
    }

    /// The command's life after its start: its terminal opened, its output read, its exit waited on and its terminal closed (every
    /// failure, the start's included, ends it with -1), then the exit line.
    async fn complete(
        self: Arc<Self>,
        handle: String,
        request: PtyRequest,
        output: Option<OutputListener>,
        started: watch::Sender<Started>,
        completed: watch::Sender<bool>,
    ) {
        let mut code = -1;
        match self.ptys.open(request).await {
            Ok(session) => {
                let control = session.control.clone();
                if let Some(command) = self.lock().command_mut(&handle) {
                    command.control = Some(control.clone());
                }
                started.send_replace(Some(Ok(())));
                let pump = tokio::spawn(self.clone().pump(handle.clone(), session.output, output));
                code = control.wait().await.unwrap_or(-1);
                if control.close().await.is_err() {
                    code = -1;
                }
                let _ = pump.await;
            }
            Err(error) => {
                started.send_replace(Some(Err(error.to_string())));
            }
        }
        if let Some(command) = self.lock().command_mut(&handle) {
            command.exit_code = Some(code);
            command.finished = true;
        }
        self.receive(&handle, &format!("\r\n[exit {code}]\r\n"));
        completed.send_replace(true);
    }

    async fn pump(self: Arc<Self>, handle: String, mut output: mpsc::Receiver<Vec<u8>>, listener: Option<OutputListener>) {
        let mut decoder = Utf8Decoder::default();
        while let Some(bytes) = output.recv().await {
            let text = decoder.decode(&bytes);
            self.process_output(&handle, &text, listener.as_ref());
        }
        let rest = decoder.finish();
        self.process_output(&handle, &rest, listener.as_ref());
    }

    fn process_output(&self, handle: &str, text: &str, listener: Option<&OutputListener>) {
        if text.is_empty() {
            return;
        }
        self.receive(handle, text);
        self.record(handle, text);
        if let Some(listener) = listener {
            listener(text);
        }
    }

    fn record(&self, handle: &str, text: &str) {
        let mut gate = self.lock();
        let Some(command) = gate.command_mut(handle) else { return };
        command.log.push_str(text);
        if command.log.len() > LOG_LIMIT {
            let mut cut = command.log.len() - LOG_LIMIT;
            while !command.log.is_char_boundary(cut) {
                cut += 1;
            }
            command.log.drain(..cut);
            command.log_start += cut;
        }
    }

    fn receive(&self, handle: &str, text: &str) {
        {
            let mut gate = self.lock();
            if let Some(command) = gate.command_mut(handle)
                && command.pending.push(text, BUFFER_LIMIT)
            {
                command.dropped = true;
                command.cleaner.reset();
            }
            gate.screen.push(text, BUFFER_LIMIT);
        }
        let listeners: Vec<OutputListener> =
            self.listeners.lock().unwrap_or_else(|poisoned| poisoned.into_inner()).iter().map(|(_, l)| l.clone()).collect();
        for listener in listeners {
            listener(text);
        }
    }

    /// The handles of the commands still running, oldest first.
    pub fn running_handles(&self) -> Vec<String> {
        self.lock().commands.iter().filter(|c| !c.finished).map(|c| c.handle.clone()).collect()
    }

    pub fn is_command_running(&self, handle: &str) -> bool {
        self.lock().command(handle).is_some_and(|c| !c.finished)
    }

    /// The command's own output from `offset` (a stream position in bytes this gave out, or 0) on, at most `max_chars` characters,
    /// apart from what [`AgentTerminal::read`] hands the agent (which it does not move).
    pub fn read_output(&self, handle: &str, offset: usize, max_chars: usize) -> PromptRead {
        let gate = self.lock();
        let Some(command) = gate.command(handle) else {
            return PromptRead { output: String::new(), next_offset: offset, more_remains: false };
        };
        let log = &command.log;
        let mut start = offset.clamp(command.log_start, command.log_start + log.len()) - command.log_start;
        while !log.is_char_boundary(start) {
            start += 1;
        }
        let end = log[start..].char_indices().nth(max_chars).map_or(log.len(), |(at, _)| start + at);
        PromptRead { output: log[start..end].to_string(), next_offset: command.log_start + end, more_remains: end < log.len() }
    }

    /// Types `text` into the command's terminal as the user would have; whether it was taken.
    pub fn write_to(&self, handle: &str, text: &str) -> bool {
        let control = self.lock().command(handle).filter(|c| !c.finished).and_then(|c| c.control.clone());
        control.is_some_and(|control| control.write(text.as_bytes()))
    }

    /// `read_latest_output`: what the command printed since the last read (cleaned, at most 21,000 units), and where it stands.
    pub fn read(&self, handle: &str) -> Result<AgentTerminalResult, AgentTerminalError> {
        let mut gate = self.lock();
        let command = gate.command_mut(handle).ok_or(AgentTerminalError::UnknownHandle)?;
        let chunk = command.pending.take_front(READ_LIMIT);
        let output = command.cleaner.clean(&chunk);
        let result = AgentTerminalResult {
            handle: handle.to_string(),
            status: if command.finished { "done" } else { "running" },
            output,
            exit_code: if command.finished { command.exit_code } else { None },
            output_dropped: command.dropped,
            more_remains: !command.pending.is_empty(),
        };
        command.dropped = false;
        Ok(result)
    }

    /// `stop`: Ctrl+C, then the command's processes ended after 3 s; what it printed since the last read.
    pub async fn stop(&self, handle: &str) -> Result<AgentTerminalResult, AgentTerminalError> {
        let (started, completed) = {
            let gate = self.lock();
            let command = gate.command(handle).ok_or(AgentTerminalError::UnknownHandle)?;
            (command.started.clone(), command.completed.clone())
        };
        let _ = wait_started(started).await;
        let control = self.lock().command(handle).filter(|c| !c.finished).and_then(|c| c.control.clone());
        if let Some(control) = control {
            control.write(b"\x03");
            if tokio::time::timeout(STOP_GRACE, wait_completed(completed.clone())).await.is_err() {
                let _ = control.close().await;
            }
            wait_completed(completed).await;
        }
        self.read(handle)
    }

    /// Types into the newest command (the terminal pane's keyboard); whether it was taken.
    pub fn write(&self, text: &str) -> bool {
        let control = {
            let gate = self.lock();
            gate.active.as_deref().and_then(|active| gate.command(active)).and_then(|c| c.control.clone())
        };
        control.is_some_and(|control| control.write(text.as_bytes()))
    }

    /// Every running command's grid (the terminal pane's size).
    pub fn resize(&self, columns: i32, rows: i32) -> Result<(), String> {
        let controls: Vec<Arc<dyn PtyControl>> =
            self.lock().commands.iter().filter(|c| !c.finished).filter_map(|c| c.control.clone()).collect();
        let size = PtySize { columns: columns.clamp(2, 1000) as u16, rows: rows.clamp(1, 1000) as u16 };
        for control in controls {
            control.resize(size).map_err(|error| error.to_string())?;
        }
        Ok(())
    }

    /// Someone who sees everything printed from now on; the id to stop with.
    pub fn subscribe(&self, listener: OutputListener) -> u64 {
        let id = self.next_listener.fetch_add(1, Ordering::SeqCst);
        self.listeners.lock().unwrap_or_else(|poisoned| poisoned.into_inner()).push((id, listener));
        id
    }

    pub fn unsubscribe(&self, id: u64) {
        self.listeners.lock().unwrap_or_else(|poisoned| poisoned.into_inner()).retain(|(listener, _)| *listener != id);
    }

    /// Ends every command (their terminals closed, all at once: a command still starting holds up none of the others) and refuses new
    /// ones. A second call returns at once.
    pub async fn dispose(self: &Arc<Self>) {
        let commands: Vec<(watch::Receiver<Started>, watch::Receiver<bool>, String)> = {
            let mut gate = self.lock();
            if gate.closing {
                return;
            }
            gate.closing = true;
            gate.commands.iter().map(|c| (c.started.clone(), c.completed.clone(), c.handle.clone())).collect()
        };
        let mut endings = vec![];
        for (started, completed, handle) in commands {
            let terminal = self.clone();
            endings.push(tokio::spawn(async move {
                let _ = wait_started(started).await;
                let control = terminal.lock().command(&handle).and_then(|c| c.control.clone());
                if let Some(control) = control {
                    let _ = control.close().await;
                }
                wait_completed(completed).await;
            }));
        }
        for ending in endings {
            let _ = ending.await;
        }
    }
}

impl PromptTerminal for AgentTerminal {
    fn running_handles(&self) -> Vec<String> {
        AgentTerminal::running_handles(self)
    }
    fn read_output(&self, handle: &str, offset: usize, max_chars: usize) -> PromptRead {
        AgentTerminal::read_output(self, handle, offset, max_chars)
    }
}
