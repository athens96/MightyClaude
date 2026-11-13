//! Terminal panes, the agents' terminals, terminal prompt cards and a transcript's shell block runs (W4-A; C# MainWindow.Terminal.cs,
//! the terminal half of MainWindow.WebShell.Panes.cs, MainWindow.TerminalPrompts.cs, MainWindow.ShellBlockRuns.cs and the
//! terminal half of MainWindow.AgentIO.cs), over mighty-terminal.
//!
//! - Methods: `terminalOpen` (the footer; from then on a `footer` push whenever it changes), `terminalMessage` (the page's xterm:
//!   ready, input, resize, title, directory, copy, paste, pressed), `terminalRestart`, `terminalHistory`, `terminalPromptAnswer`,
//!   `terminalPromptShow`, `shellBlockRun`, `shellBlockStop`.
//! - Pushes: `{kind:'terminal', paneId, message}` — output (each 32 ms, base64 UTF-8), paste, reset, font, theme, footer.
//! - A shell pane's status follows its shell (`RunService::update_pane`, C# `SetShellStatus`: idle, running — read every 500 ms —
//!   completed, error), as a delta like any other change.
//! - A terminal goes with its pane (looked at every quarter second) and every terminal closes when the app quits, each close on its
//!   own thread and the quit waiting at most a few seconds for them.
//!
//! What later stages give stays at rest ([`Pending`]).

use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, Weak};
use std::time::Duration;

use mighty_composer::cards::TerminalPromptInput;
use mighty_core::add_pane::{save_layout, save_mode};
use mighty_core::layout;
use mighty_core::model::{AppSnapshot, PaneLayoutNode, RunSession};
use mighty_core::shell::effective_layout;
use mighty_core::wire;
use mighty_platform::{Os, Platform};
use mighty_run::service::PaneSave;
use mighty_terminal::history::terminal_history;
use mighty_terminal::pane::{ACTIVITY_INTERVAL, FLUSH_INTERVAL};
use mighty_terminal::prompt::{TerminalPrompt, TerminalPromptAnswer, TerminalPromptMonitor};
use mighty_terminal::shell_blocks::{self, ShellBlockOutput};
use mighty_terminal::{
    AgentTerminal, PaneEvents, PaneKind, PaneOptions, Ports, PromptTerminal, TerminalFont, TerminalMessage, TerminalPane,
};
use serde_json::{Value, json};
use tauri::Manager;

use crate::frame::{activate_pane, select_session};
use crate::host::{Host, Registry};
use crate::methods::text;

/// What a later stage gives the terminals; until then each stays at rest.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Pending {
    /// The shell pane's history dialog is a native panel (C# `ShowTerminalHistory`, host-contract D14): `terminalHistory` makes its
    /// content (mighty-terminal `history`) and shows nothing until W6's NativeNode forms.
    HistoryDialog,
    /// The agent's `run_in_terminal` / `read_latest_output` / `stop` (Agent IO MCP, W7): its terminals are [`Terminals::agent`]'s.
    AgentIoTools,
    /// A command the app types into a new shell pane (C# `RunTerminalCommandAsync`: style installs, sign-ins; W6/W9): the pane is
    /// frame.rs's `add_pane`, and [`TerminalPane::write`] with `TerminalInput::apply` is its typing.
    RunTerminalCommand,
    /// The phone's view of a shell's status and of a terminal prompt (C# `mobileRouter.Changed`, `mobilePush`; W8).
    Mobile,
}

pub const PENDING: [Pending; 4] = [Pending::HistoryDialog, Pending::AgentIoTools, Pending::RunTerminalCommand, Pending::Mobile];

/// How often an agent terminal is looked at for a prompt, and how many looks with no command running end the watch.
const PROMPT_INTERVAL: Duration = Duration::from_millis(400);
const PROMPT_IDLE_POLLS: u32 = 8;
/// A running shell block's record takes its output at most this often.
const BLOCK_FLUSH: Duration = Duration::from_millis(200);
/// How long the quit waits for the terminals to close (their processes die with the app's job handles anyway).
const QUIT_WAIT: Duration = Duration::from_secs(5);

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

#[derive(Default)]
struct Prompts {
    monitors: HashMap<String, Arc<TerminalPromptMonitor>>,
    /// The card each agent pane shows now (C# `terminalPrompts`).
    current: HashMap<String, TerminalPrompt>,
    watching: bool,
}

/// A transcript's shell block running in its agent pane's terminal (C# `ShellBlockRunning`).
struct BlockRun {
    pane: String,
    key: String,
    terminal: Arc<AgentTerminal>,
    output: Mutex<ShellBlockOutput>,
    handle: Mutex<Option<String>>,
    stopping: AtomicBool,
    flush_queued: AtomicBool,
}

type Agents = Arc<Mutex<HashMap<String, Arc<AgentTerminal>>>>;

/// Every terminal of the window (C# `views`' terminal halves, `agentTerminals`, `terminalPrompts`, `shellBlockRunning`).
pub struct Terminals {
    host: Weak<Host>,
    ports: Ports,
    events: Arc<HostEvents>,
    panes: Mutex<HashMap<String, Arc<TerminalPane>>>,
    agents: Agents,
    prompts: Mutex<Prompts>,
    blocks: Mutex<HashMap<String, Arc<BlockRun>>>,
    /// The smoke's own terminal panes: in a smoke no other pane starts a shell (C# `smokeTerminalEnabled`).
    smoke_allowed: Mutex<HashSet<String>>,
    closing: AtomicBool,
}

/// What a pane asks of the window, answered from the host.
struct HostEvents {
    host: Weak<Host>,
    agents: Agents,
}

impl PaneEvents for HostEvents {
    fn post(&self, pane: &str, message: &TerminalMessage) {
        let Some(host) = self.host.upgrade() else { return };
        let json = format!(
            "{{\"kind\":\"terminal\",\"paneId\":{},\"message\":{}}}",
            serde_json::to_string(pane).unwrap_or_default(),
            serde_json::to_string(message).unwrap_or_default()
        );
        let view = host.view();
        host.push(&view, "terminal", json);
    }

    fn status(&self, pane: &str, status: &'static str) {
        if let Some(host) = self.host.upgrade() {
            set_shell_status(&host, pane, status);
        }
    }

    fn session_status(&self, pane: &str) -> Option<String> {
        let host = self.host.upgrade()?;
        let view = host.view();
        view.snapshot.session(pane).map(|s| s.status().to_string())
    }

    fn error(&self, text: String) {
        if let Some(host) = self.host.upgrade() {
            host.view().error = Some(text);
            host.schedule();
        }
    }

    fn pressed(&self, pane: &str) {
        if let Some(host) = self.host.upgrade() {
            let _ = activate_pane(&mut host.view(), pane);
            host.schedule();
        }
    }

    fn light(&self) -> bool {
        self.host.upgrade().is_some_and(|host| host.view().snapshot.theme() == "light")
    }

    /// The text setting (frame.rs, C# `AppText.Current`): its terminal size and code font.
    fn font(&self) -> TerminalFont {
        let Some(host) = self.host.upgrade() else { return TerminalFont::default() };
        let text = crate::frame::text_setting(&host);
        TerminalFont { size: text.terminal_size(), family: text.code_family }
    }

    fn agent_terminal(&self, owner: &str) -> Option<Arc<AgentTerminal>> {
        lock(&self.agents).get(owner).cloned()
    }
}

/// C# `SetShellStatus` through the run service (a light update of the pane), then the page's copy taken again.
fn set_shell_status(host: &Arc<Host>, pane: &str, status: &'static str) {
    let Some(runs) = host.runs() else { return };
    let changed = runs.service.update_pane(pane, |session| {
        mighty_terminal::activity::shell_status_change(session, status)
            .map(|(next, now)| (next, if now { PaneSave::Now } else { PaneSave::Later }))
    });
    if changed {
        host.view().sync();
        host.schedule();
    }
}

/// C# `DesktopService.UpdateShellBlockRun` through the run service.
fn update_block_record(
    host: &Arc<Host>,
    pane: &str,
    key: &str,
    update: impl FnOnce(Option<&mighty_core::model::ShellBlockRun>) -> Option<mighty_core::model::ShellBlockRun>,
) {
    let Some(runs) = host.runs() else { return };
    let changed = runs.service.update_pane(pane, |session| {
        shell_blocks::update_pane(session, key, update).map(|(next, ended)| (next, if ended { PaneSave::Now } else { PaneSave::Soon }))
    });
    if changed {
        host.view().sync();
        host.schedule();
    }
}

/// `AgentIOPaneKind.Terminal`.
const AGENT_TERMINAL: &str = "agent-terminal";

/// C# `WithAgentIOPane`: the state with the agent pane's terminal (or browser) pane to its right, titled as on the Mac; unchanged
/// while that pane is already open.
fn with_agent_io_pane(
    state: &mut AppSnapshot,
    defaults: &mut HashMap<String, PaneLayoutNode>,
    agent: &str,
    workspace: &str,
    kind: &str,
    locale: mighty_core::locale::Locale,
) -> Result<(), String> {
    let pane_id = format!("{kind}:{agent}");
    let Some(parent) = state.sessions().iter().find(|p| p.id() == agent && p.workspace_id() == workspace).cloned() else {
        return Err("Agent pane closed.".into());
    };
    if state.session(&pane_id).is_some() {
        return Ok(());
    }
    if state.sessions().len() >= layout::MAXIMUM_SESSIONS {
        return Err("Close a pane before opening another.".into());
    }
    let before = effective_layout(state, workspace, defaults);
    let target = before
        .as_ref()
        .and_then(|tree| layout::groups(tree).into_iter().find(|g| g.session_ids().any(|id| id == agent)).map(|g| g.id().to_string()));
    let title = if kind == AGENT_TERMINAL { "agentTerminal.terminalPane.title" } else { "agentTerminal.browserPane.title" };
    let pane = RunSession {
        id: Some(pane_id.clone()),
        kind: Some(kind.into()),
        owner_session_id: Some(agent.into()),
        workspace_id: Some(workspace.into()),
        workspace_profile_key: Some(workspace.into()),
        provider: parent.provider.clone(),
        title: Some(locale.get(title)),
        ..RunSession::default()
    };
    state.sessions_mut().push(Arc::new(pane));
    let mut tree = effective_layout(state, workspace, defaults);
    if let (Some(node), Some(target)) = (&tree, &target) {
        tree = Some(layout::move_tab(node, &pane_id, target, "right", None));
    }
    save_layout(state, workspace, tree);
    save_mode(state, workspace, "custom");
    Ok(())
}

/// [`with_agent_io_pane`] as a change of the state.
fn show_agent_io_pane(host: &Host, agent: &str, workspace: &str, kind: &str) -> Result<(), String> {
    let mut view = host.view();
    let mut defaults = view.layout_defaults.clone();
    let mut failure = None;
    view.update(|state| {
        if let Err(error) = with_agent_io_pane(state, &mut defaults, agent, workspace, kind, host.locale) {
            failure = Some(error);
        }
    })?;
    view.layout_defaults = defaults;
    failure.map_or(Ok(()), Err)
}

/// C# `LocalPath(workspace)`: the workspace's folder on this computer, None for a remote one.
fn local_folder(state: &AppSnapshot, workspace: &str) -> Option<PathBuf> {
    let workspace = state.workspace(workspace)?;
    (workspace.remote.is_none() && !workspace.path().is_empty()).then(|| PathBuf::from(workspace.path()))
}

/// C# `DelegationPanes.IsWorkingFolder`: a delegated child's worktree, a full path of at most 4,096 characters.
fn is_working_folder(path: &str) -> bool {
    wire::utf16_len(path) <= 4096 && !path.contains('\0') && Path::new(path).is_absolute()
}

impl Terminals {
    /// The window's terminals, started with the host: managed by the app, the 32 ms output clock (and a pane's teardown when it
    /// leaves the state) and the 500 ms shell-activity reading on threads of their own.
    pub fn start(host: &Arc<Host>, platform: &Platform) -> Arc<Terminals> {
        let agents: Agents = Arc::default();
        let events = Arc::new(HostEvents { host: Arc::downgrade(host), agents: agents.clone() });
        let ports = Ports {
            ptys: platform.ptys.clone(),
            environment: platform.environment.clone(),
            clipboard: crate::platform::clipboard(),
            locale: host.locale,
            engine: match platform.profile.os {
                Os::Windows => "ConPTY".into(),
                Os::Macos => "Ghostty".into(),
            },
        };
        let terminals = Arc::new(Terminals {
            host: Arc::downgrade(host),
            ports,
            events,
            panes: Mutex::default(),
            agents,
            prompts: Mutex::default(),
            blocks: Mutex::default(),
            smoke_allowed: Mutex::default(),
            closing: AtomicBool::new(false),
        });
        host.app.manage(terminals.clone());
        host.log.line(&format!("terminals: {} engine; at rest until their stages: {PENDING:?}", terminals.ports.engine));
        let clock = Arc::downgrade(&terminals);
        std::thread::Builder::new()
            .name("terminal-clock".into())
            .spawn(move || {
                let mut ticks = 0u64;
                while let Some(terminals) = clock.upgrade() {
                    if terminals.closing.load(Ordering::SeqCst) {
                        return;
                    }
                    for pane in terminals.all() {
                        pane.tick();
                    }
                    ticks += 1;
                    if ticks.is_multiple_of(8) {
                        terminals.reconcile();
                    }
                    drop(terminals);
                    std::thread::sleep(FLUSH_INTERVAL);
                }
            })
            .expect("the terminal clock");
        let reading = Arc::downgrade(&terminals);
        std::thread::Builder::new()
            .name("shell-activity".into())
            .spawn(move || {
                while let Some(terminals) = reading.upgrade() {
                    if terminals.closing.load(Ordering::SeqCst) {
                        return;
                    }
                    for pane in terminals.all() {
                        pane.read_activity();
                    }
                    drop(terminals);
                    std::thread::sleep(ACTIVITY_INTERVAL);
                }
            })
            .expect("the shell-activity reading");
        terminals
    }

    fn all(&self) -> Vec<Arc<TerminalPane>> {
        lock(&self.panes).values().cloned().collect()
    }

    /// C# `RefreshTerminalTheme` for every pane (`Retheme`, `TextAppearanceChanged`): the theme or the text setting changed.
    pub fn restyle(&self) {
        for pane in self.all() {
            pane.restyle();
        }
    }

    /// The terminal of a shell or agent-terminal pane, made the first time (C# `WebTerminalPane`); "Session not found." for any
    /// other id.
    pub fn pane(&self, host: &Host, id: Option<&str>) -> Result<Arc<TerminalPane>, String> {
        let not_found = || "Session not found.".to_string();
        let id = id.ok_or_else(not_found)?;
        let (kind, folder) = {
            let view = host.view();
            let session = view.snapshot.session(id).filter(|s| matches!(s.kind(), "shell" | AGENT_TERMINAL)).ok_or_else(not_found)?;
            let kind =
                if session.kind() == "shell" { PaneKind::Shell } else { PaneKind::Agent { owner: session.owner_session_id.clone() } };
            (kind, local_folder(&view.snapshot, session.workspace_id()))
        };
        let mut panes = lock(&self.panes);
        if let Some(pane) = panes.get(id).filter(|pane| *pane.kind() == kind) {
            return Ok(pane.clone());
        }
        let enabled = host.smoke.is_none() || lock(&self.smoke_allowed).contains(id);
        let options = PaneOptions { id: id.to_string(), kind, folder, enabled, no_profile: host.smoke.is_some() };
        let pane = TerminalPane::new(options, self.ports.clone(), self.events.clone());
        if let Some(earlier) = panes.insert(id.to_string(), pane.clone()) {
            tauri::async_runtime::spawn(async move { earlier.dispose().await });
        }
        Ok(pane)
    }

    /// An agent pane's terminal, made the first time in `directory` (C# `agentTerminals`; Agent IO's `run_in_terminal` makes it too,
    /// W7).
    pub fn agent(&self, pane: &str, directory: &Path) -> Arc<AgentTerminal> {
        lock(&self.agents)
            .entry(pane.to_string())
            .or_insert_with(|| AgentTerminal::new(directory.to_path_buf(), self.ports.ptys.clone(), self.ports.environment.clone()))
            .clone()
    }

    /// A pane's terminal goes once the pane leaves the state, an agent's terminal once its agent pane does (C# `CloseTerminal`,
    /// `ReconcileAgentIO`); each close on its own task.
    fn reconcile(&self) {
        let Some(host) = self.host.upgrade() else { return };
        let (terminal_panes, agent_panes): (HashSet<String>, HashSet<String>) = {
            let view = host.view();
            let sessions = view.snapshot.sessions();
            (
                sessions.iter().filter(|s| matches!(s.kind(), "shell" | AGENT_TERMINAL)).map(|s| s.id().to_string()).collect(),
                sessions.iter().filter(|s| s.kind() == "claude").map(|s| s.id().to_string()).collect(),
            )
        };
        let gone: Vec<Arc<TerminalPane>> = {
            let mut panes = lock(&self.panes);
            let ids: Vec<String> = panes.keys().filter(|id| !terminal_panes.contains(*id)).cloned().collect();
            ids.iter().filter_map(|id| panes.remove(id)).collect()
        };
        for pane in gone {
            tauri::async_runtime::spawn(async move { pane.dispose().await });
        }
        let ended: Vec<Arc<AgentTerminal>> = {
            let mut agents = lock(&self.agents);
            let ids: Vec<String> = agents.keys().filter(|id| !agent_panes.contains(*id)).cloned().collect();
            ids.iter().filter_map(|id| agents.remove(id)).collect()
        };
        for agent in ended {
            tauri::async_runtime::spawn(async move { agent.dispose().await });
        }
    }

    /// Every terminal closed (the app quits): at once, all together, the wait bounded.
    pub async fn close_all(&self) {
        self.closing.store(true, Ordering::SeqCst);
        let panes: Vec<Arc<TerminalPane>> = lock(&self.panes).drain().map(|(_, pane)| pane).collect();
        let agents: Vec<Arc<AgentTerminal>> = lock(&self.agents).drain().map(|(_, agent)| agent).collect();
        let mut closes = vec![];
        for pane in panes {
            closes.push(tauri::async_runtime::spawn(async move { pane.dispose().await }));
        }
        for agent in agents {
            closes.push(tauri::async_runtime::spawn(async move { agent.dispose().await }));
        }
        let _ = tokio::time::timeout(QUIT_WAIT, async {
            for close in closes {
                let _ = close.await;
            }
        })
        .await;
    }

    // ── terminal prompts (C# MainWindow.TerminalPrompts.cs) ──────────────────────────────────────────────────────────────────

    /// Starts following the agent terminals for prompts, unless it already does; it ends by itself once no agent terminal runs a
    /// command for eight looks.
    fn watch_prompts(self: &Arc<Self>) {
        {
            let mut prompts = lock(&self.prompts);
            if prompts.watching || self.closing.load(Ordering::SeqCst) {
                return;
            }
            prompts.watching = true;
        }
        let terminals = self.clone();
        tauri::async_runtime::spawn(async move {
            let mut idle = 0;
            while idle < PROMPT_IDLE_POLLS {
                tokio::time::sleep(PROMPT_INTERVAL).await;
                if terminals.closing.load(Ordering::SeqCst) {
                    break;
                }
                idle = if terminals.poll_prompts() { 0 } else { idle + 1 };
            }
            lock(&terminals.prompts).watching = false;
        });
    }

    /// One look at every agent terminal; whether any command still runs.
    fn poll_prompts(&self) -> bool {
        let agents: Vec<(String, Arc<AgentTerminal>)> = lock(&self.agents).iter().map(|(id, t)| (id.clone(), t.clone())).collect();
        let mut running = false;
        let mut found = HashMap::new();
        for (pane, terminal) in &agents {
            if !terminal.running_handles().is_empty() {
                running = true;
            }
            let monitor = lock(&self.prompts).monitors.entry(pane.clone()).or_default().clone();
            if let Some(prompt) = monitor.poll(terminal.as_ref() as &dyn PromptTerminal) {
                found.insert(pane.clone(), prompt);
            }
        }
        let changed: Vec<String> = {
            let mut prompts = lock(&self.prompts);
            prompts.monitors.retain(|id, _| agents.iter().any(|(pane, _)| pane == id));
            let mut ids: HashSet<String> = found.keys().cloned().collect();
            ids.extend(prompts.current.keys().cloned());
            let changed: Vec<String> =
                ids.into_iter().filter(|id| found.get(id).map(|p| &p.id) != prompts.current.get(id).map(|p| &p.id)).collect();
            if !changed.is_empty() {
                prompts.current = found;
            }
            changed
        };
        for id in &changed {
            self.prompt_changed(id);
        }
        running
    }

    /// The page redraws the pane's cards.
    fn prompt_changed(&self, pane: &str) {
        if let Some(host) = self.host.upgrade()
            && let Some(ui) = host.view().panes.get_mut(pane)
        {
            ui.dirty = true;
        }
    }

    /// C# `AnswerTerminalPrompt`: the answer goes to the waiting command, and the card goes until it asks again. Whether it was
    /// typed.
    fn answer_prompt(&self, pane: &str, prompt_id: &str, answer: &TerminalPromptAnswer) -> bool {
        let Some(prompt) = lock(&self.prompts).current.get(pane).filter(|p| p.id == prompt_id).cloned() else { return false };
        let Some(terminal) = lock(&self.agents).get(pane).cloned() else { return false };
        if !terminal.write_to(&prompt.handle, &answer.input()) {
            return false;
        }
        {
            let mut prompts = lock(&self.prompts);
            if let Some(monitor) = prompts.monitors.get(pane) {
                monitor.answered(&prompt);
            }
            prompts.current.remove(pane);
        }
        self.prompt_changed(pane);
        true
    }

    /// The card a pane shows now, as the cards model takes it.
    pub fn prompt_input(&self, pane: &str) -> Option<TerminalPromptInput> {
        lock(&self.prompts).current.get(pane).map(|prompt| TerminalPromptInput {
            id: prompt.id.clone(),
            text: prompt.text.clone(),
            options: prompt.options.clone(),
            kind: prompt.kind.to_string(),
            default_yes: prompt.default_yes,
        })
    }

    /// C# `OpenAgentTerminalPane`: the agent's terminal pane put back beside it when it was closed, and selected. The terminal kept
    /// running, so the pane shows its output again.
    fn open_agent_terminal_pane(&self, host: &Host, agent: &str) -> Result<(), String> {
        let workspace = {
            let view = host.view();
            match view.snapshot.session(agent) {
                Some(pane) if lock(&self.agents).contains_key(agent) => pane.workspace_id().to_string(),
                _ => return Ok(()),
            }
        };
        show_agent_io_pane(host, agent, &workspace, AGENT_TERMINAL)?;
        select_session(&mut host.view(), &format!("{AGENT_TERMINAL}:{agent}"))
    }

    // ── shell block runs (C# MainWindow.ShellBlockRuns.cs) ───────────────────────────────────────────────────────────────────

    /// `shellBlockRun{paneId, key, command}`: the command, which must be a line-for-line copy of what that assistant entry shows,
    /// runs in the agent pane's own terminal in the workspace's folder (a delegated child's worktree), and its terminal pane opens
    /// beside the agent. One block runs at a time per pane. The run is the user's own click: it does not pass the agent's tool
    /// permissions.
    async fn run_shell_block(
        self: &Arc<Self>,
        host: &Arc<Host>,
        pane: Option<&str>,
        key: Option<&str>,
        command: Option<&str>,
    ) -> Result<(), String> {
        let command = command.map(|c| c.trim_end_matches(['\r', '\n']));
        let (Some(pane_id), Some(key), Some(command)) = (pane, key, command) else { return Ok(()) };
        if self.closing.load(Ordering::SeqCst)
            || !wire::identifier(pane_id)
            || !shell_blocks::is_key(Some(key))
            || !shell_blocks::is_command(Some(command))
        {
            return Ok(());
        }
        let (workspace, directory) = {
            let view = host.view();
            let Some(pane) = view.snapshot.session(pane_id).filter(|p| p.kind() == "claude") else { return Ok(()) };
            // No pane tools for a remote workspace: its folder is on another computer.
            let Some(folder) = local_folder(&view.snapshot, pane.workspace_id()) else { return Ok(()) };
            if !shell_blocks::shown(pane, key, command) || lock(&self.blocks).contains_key(pane_id) {
                return Ok(());
            }
            // A delegated child works in its worktree, as its runs and its run_in_terminal do.
            let directory = match pane.working_folder.as_deref() {
                Some(path) if is_working_folder(path) && Path::new(path).is_dir() => PathBuf::from(path),
                _ => folder,
            };
            (pane.workspace_id().to_string(), directory)
        };
        let terminal = self.agent(pane_id, &directory);
        show_agent_io_pane(host, pane_id, &workspace, AGENT_TERMINAL)?;
        host.view().shows_dashboard = false;
        let running = {
            let mut blocks = lock(&self.blocks);
            // The change above let another click in: still one block at a time.
            if self.closing.load(Ordering::SeqCst) || blocks.contains_key(pane_id) {
                return Ok(());
            }
            let running = Arc::new(BlockRun {
                pane: pane_id.to_string(),
                key: key.to_string(),
                terminal: terminal.clone(),
                output: Mutex::new(ShellBlockOutput::default()),
                handle: Mutex::new(None),
                stopping: AtomicBool::new(false),
                flush_queued: AtomicBool::new(false),
            });
            blocks.insert(pane_id.to_string(), running.clone());
            running
        };
        let started = shell_blocks::started(key, command, None);
        update_block_record(host, pane_id, key, |_| Some(started));
        let listener = {
            let (running, host) = (running.clone(), Arc::downgrade(host));
            Arc::new(move |raw: &str| receive_block_output(&host, &running, raw))
        };
        let handle = match terminal.start(command, Some(listener)).await {
            Ok(handle) => handle,
            Err(error) => {
                lock(&self.blocks).remove(pane_id);
                update_block_record(host, pane_id, key, |run| {
                    run.filter(|r| r.state.as_deref() == Some("running")).map(|r| shell_blocks::finished(r, None, false, None))
                });
                return Err(error.to_string());
            }
        };
        *lock(&running.handle) = Some(handle.clone());
        self.watch_prompts();
        let (terminals, finishing, host_for_finish) = (self.clone(), running.clone(), host.clone());
        tauri::async_runtime::spawn(async move { terminals.finish_shell_block(&host_for_finish, finishing, handle).await });
        // A Stop pressed while the command was still starting is kept and done now.
        let handle = lock(&running.handle).clone();
        if running.stopping.load(Ordering::SeqCst)
            && let Some(handle) = handle
        {
            let _ = running.terminal.stop(&handle).await;
        }
        Ok(())
    }

    /// C# `FinishShellBlock`: once the command ended, the record takes its last output and its end. Quitting, the record stays
    /// running and the next launch reads it interrupted.
    async fn finish_shell_block(&self, host: &Arc<Host>, running: Arc<BlockRun>, handle: String) {
        let code = running.terminal.exited(&handle).await.unwrap_or(Some(-1));
        {
            let mut blocks = lock(&self.blocks);
            if blocks.get(&running.pane).is_some_and(|current| Arc::ptr_eq(current, &running)) {
                blocks.remove(&running.pane);
            }
        }
        if self.closing.load(Ordering::SeqCst) {
            return;
        }
        let output = lock(&running.output).text();
        let stopped = running.stopping.load(Ordering::SeqCst);
        update_block_record(host, &running.pane, &running.key, |run| {
            run.filter(|r| r.state.as_deref() == Some("running")).map(|r| {
                let with_output = mighty_core::model::ShellBlockRun { output: Some(output), ..r.clone() };
                shell_blocks::finished(&with_output, code, stopped, None)
            })
        });
    }

    /// `shellBlockStop{paneId, key}`: as `stop` stops an agent's command (Ctrl+C, then the process ended after 3 s).
    async fn stop_shell_block(&self, pane: Option<&str>, key: Option<&str>) {
        let Some(running) = pane.and_then(|pane| lock(&self.blocks).get(pane).cloned()) else { return };
        if Some(running.key.as_str()) != key {
            return;
        }
        running.stopping.store(true, Ordering::SeqCst);
        // Still starting: the run stops it once the terminal gave its handle.
        let handle = lock(&running.handle).clone();
        if let Some(handle) = handle {
            let _ = running.terminal.stop(&handle).await;
        }
    }

    // ── the smoke's terminal step ────────────────────────────────────────────────────────────────────────────────────────────

    /// The shell a new terminal runs (the smoke types in its language).
    pub fn default_shell(&self) -> PathBuf {
        self.ports.ptys.default_shell()
    }

    /// The smoke's own pane may start a shell.
    pub fn smoke_allow(&self, pane: &str) {
        lock(&self.smoke_allowed).insert(pane.to_string());
    }

    /// The pane's terminal as the smoke reads it: its status in the state, its footer and the host's diagnostics; null before the
    /// pane has a terminal.
    pub fn smoke_state(&self, host: &Host, pane: &str) -> Value {
        let status = host.view().snapshot.session(pane).map(|s| s.status().to_string());
        let Some(terminal) = lock(&self.panes).get(pane).cloned() else { return json!({"status": status}) };
        json!({
            "status": status, "running": terminal.is_running(), "closed": terminal.is_closed(),
            "footer": terminal.footer(), "diagnostics": terminal.diagnostics(),
        })
    }

    /// The smoke's pane closed: its terminal ends at once (as the pane's close would end it); whether it was there.
    pub async fn smoke_close(&self, pane: &str) -> bool {
        let terminal = lock(&self.panes).remove(pane);
        match terminal {
            Some(terminal) => {
                let _ = tokio::time::timeout(Duration::from_secs(20), terminal.dispose()).await;
                terminal.is_closed() && !terminal.is_running()
            }
            None => false,
        }
    }
}

/// C# `ReceiveShellBlockOutput`: the output is cleaned as it comes and the record takes it at most every 200 ms.
fn receive_block_output(host: &Weak<Host>, running: &Arc<BlockRun>, raw: &str) {
    lock(&running.output).append(raw);
    if running.flush_queued.swap(true, Ordering::SeqCst) {
        return;
    }
    let (host, running) = (host.clone(), running.clone());
    tauri::async_runtime::spawn(async move {
        tokio::time::sleep(BLOCK_FLUSH).await;
        running.flush_queued.store(false, Ordering::SeqCst);
        let output = lock(&running.output).text();
        if let Some(host) = host.upgrade() {
            update_block_record(&host, &running.pane, &running.key, |run| {
                run.filter(|r| r.state.as_deref() == Some("running") && r.output.as_deref() != Some(output.as_str()))
                    .map(|r| mighty_core::model::ShellBlockRun { output: Some(output.clone()), ..r.clone() })
            });
        }
    });
}

/// The window's terminals (none before the host started them).
pub fn terminals(host: &Host) -> Result<Arc<Terminals>, String> {
    host.app.try_state::<Arc<Terminals>>().map(|state| state.inner().clone()).ok_or_else(|| "the terminals are not ready".to_string())
}

/// The theme or the text setting changed (methods.rs's `setTheme`, frame.rs's `textSize`): every terminal takes its look again.
pub fn restyle(host: &Host) {
    if let Ok(terminals) = terminals(host) {
        terminals.restyle();
    }
}

/// The terminal prompt card of a pane, for the cards model (panes.rs).
pub fn prompt_input(host: &Host, pane: &str) -> Option<TerminalPromptInput> {
    terminals(host).ok()?.prompt_input(pane)
}

/// Every terminal closed while the app quits (main.rs), the wait bounded.
pub fn close_all(app: &tauri::AppHandle) {
    if let Some(terminals) = app.try_state::<Arc<Terminals>>() {
        let terminals = terminals.inner().clone();
        tauri::async_runtime::block_on(terminals.close_all());
    }
}

/// C# `Act`: the error line cleared, then whatever the action says went wrong shown there.
fn act(host: &Host, result: Result<(), String>) {
    if let Err(error) = result {
        host.view().error = Some(error);
    }
}

pub fn register(methods: &mut Registry) {
    methods.add("terminalOpen", |host: Arc<Host>, args| async move {
        let pane = terminals(&host)?.pane(&host, text(&args, "paneId"))?;
        Ok(serde_json::to_value(pane.open()).unwrap_or(Value::Null))
    });
    methods.add("terminalMessage", |host: Arc<Host>, args| async move {
        let pane = terminals(&host)?.pane(&host, text(&args, "paneId"))?;
        if let Some(message) = args.get("message").filter(|m| m.is_object()) {
            pane.handle_message(message).await;
        }
        Ok(Value::Null)
    });
    methods.add("terminalRestart", |host: Arc<Host>, args| async move {
        let pane = terminals(&host)?.pane(&host, text(&args, "paneId"))?;
        pane.restart().await;
        Ok(Value::Null)
    });
    methods.add("terminalHistory", |host: Arc<Host>, args| async move {
        // C# ShowTerminalHistory: a native panel dialog (W6's NativeNode forms, `Pending::HistoryDialog`); its content is made here.
        let pane = text(&args, "paneId").and_then(|id| host.view().snapshot.session(id).filter(|s| s.kind() == "shell").cloned());
        if let Some(pane) = pane {
            let history = terminal_history(&pane, &host.locale);
            host.log.line(&format!(
                "terminalHistory: \"{}\" ({} entries, {} characters) is a native dialog, W6's ({:?}); not shown",
                history.title,
                pane.logs().len(),
                history.text.chars().count(),
                Pending::HistoryDialog
            ));
        }
        Ok(Value::Null)
    });
    methods.add("terminalPromptAnswer", |host: Arc<Host>, args| async move {
        let answer = TerminalPromptAnswer::from_phone(text(&args, "promptId"), text(&args, "answer"), text(&args, "text"))
            .ok_or_else(|| host.locale.get("terminalPrompt.error.badAnswer"))?;
        let terminals = terminals(&host)?;
        if !terminals.answer_prompt(text(&args, "paneId").unwrap_or(""), text(&args, "promptId").unwrap_or(""), &answer) {
            return Err(host.locale.get("terminalPrompt.error.gone"));
        }
        Ok(Value::Null)
    });
    methods.add("terminalPromptShow", |host: Arc<Host>, args| async move {
        host.view().error = None;
        let result = terminals(&host).and_then(|t| t.open_agent_terminal_pane(&host, text(&args, "paneId").unwrap_or("")));
        act(&host, result);
        Ok(Value::Null)
    });
    methods.add("shellBlockRun", |host: Arc<Host>, args| async move {
        host.view().error = None;
        let result = match terminals(&host) {
            Ok(t) => t.run_shell_block(&host, text(&args, "paneId"), text(&args, "key"), text(&args, "command")).await,
            Err(error) => Err(error),
        };
        act(&host, result);
        Ok(Value::Null)
    });
    methods.add("shellBlockStop", |host: Arc<Host>, args| async move {
        host.view().error = None;
        if let Ok(terminals) = terminals(&host) {
            terminals.stop_shell_block(text(&args, "paneId"), text(&args, "key")).await;
        }
        Ok(Value::Null)
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    use mighty_core::locale::Locale;
    use mighty_core::model::Workspace;

    fn state() -> AppSnapshot {
        let workspace = Workspace { id: Some("w".into()), path: Some("C:\\work".into()), ..Workspace::default() };
        let agent = RunSession {
            id: Some("agent".into()),
            workspace_id: Some("w".into()),
            provider: Some("codex".into()),
            ..RunSession::default()
        };
        let other = RunSession { id: Some("other".into()), workspace_id: Some("w".into()), ..RunSession::default() };
        AppSnapshot {
            workspaces: Some(vec![workspace]),
            sessions: Some(vec![Arc::new(agent), Arc::new(other)]),
            active_workspace_id: Some("w".into()),
            active_session_id: Some("agent".into()),
            ..AppSnapshot::default()
        }
    }

    /// C# `WithAgentIOPane`: the agent's terminal pane opens to its right, titled "Terminal", owned by the agent, in its provider;
    /// the layout becomes custom; a second open changes nothing; an agent pane that is gone is refused.
    #[test]
    fn the_agents_terminal_pane_opens_beside_it() {
        let mut snapshot = state();
        let mut defaults = HashMap::new();
        with_agent_io_pane(&mut snapshot, &mut defaults, "agent", "w", AGENT_TERMINAL, Locale::new("en")).unwrap();
        let pane = snapshot.session("agent-terminal:agent").expect("the terminal pane");
        assert_eq!(
            (pane.kind(), pane.title(), pane.owner_session_id.as_deref(), pane.provider()),
            (AGENT_TERMINAL, "Terminal", Some("agent"), "codex")
        );
        assert_eq!(pane.workspace_profile_key.as_deref(), Some("w"));
        let modes = snapshot.pane_layout_modes.clone().unwrap();
        assert_eq!(modes.get("w").cloned().flatten().as_deref(), Some("custom"));
        let tree = snapshot.pane_layouts.as_ref().and_then(|l| l.get("w").cloned().flatten()).expect("a saved layout");
        let groups = layout::groups(&tree);
        assert!(groups.len() >= 2, "a group of its own to the right: {tree:?}");
        let again = snapshot.clone();
        with_agent_io_pane(&mut snapshot, &mut defaults, "agent", "w", AGENT_TERMINAL, Locale::new("en")).unwrap();
        assert_eq!(snapshot, again, "already open: unchanged");
        assert_eq!(
            with_agent_io_pane(&mut snapshot, &mut defaults, "gone", "w", AGENT_TERMINAL, Locale::new("en")),
            Err("Agent pane closed.".into())
        );
    }

    #[test]
    fn a_working_folder_is_a_full_path() {
        assert!(is_working_folder(if cfg!(windows) { "C:\\work\\child" } else { "/work/child" }));
        assert!(!is_working_folder("relative\\child") && !is_working_folder("a\0b") && !is_working_folder(&"x".repeat(4097)));
        assert_eq!(PENDING.len(), 4);
    }
}
