//! An agent pane's terminal (C# `AgentTerminal`): its commands, what the agent reads of them, what readers with their own cursor
//! read, the stop, the limits and the close — against the fake terminal (a loopback whose shell ends when the test says), and the
//! AgentTerminal half of C# "Agent IO real ConPTY process ownership" (AgentIOVerification.cs:75-94, Windows-only in C#) against
//! ConPTY.

mod common;

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use common::*;
use mighty_platform::fake::{FakePtyHost, KILLED};
use mighty_platform::{BoxFuture, PtyHost, PtyRequest, PtySession};
use mighty_terminal::AgentTerminal;
use mighty_terminal::agent::{AgentTerminalError, MAXIMUM_RUNNING};
use mighty_terminal::prompt::TerminalPromptMonitor;

#[tokio::test(flavor = "multi_thread")]
async fn commands_run_in_terminals_of_their_own() {
    let fixture = Fixture::new("agent");
    let terminal = fixture.agent();
    for bad in ["", "  \n", "a\0b"] {
        assert_eq!(terminal.start(bad, None).await, Err(AgentTerminalError::InvalidCommand), "{bad:?}");
    }
    assert_eq!(terminal.start(&"x".repeat(65_537), None).await, Err(AgentTerminalError::InvalidCommand), "over 64 KiB");

    let seen = Arc::new(Mutex::new(String::new()));
    let sink = seen.clone();
    let handle = terminal.start("npm test", Some(Arc::new(move |text: &str| sink.lock().unwrap().push_str(text)))).await.unwrap();
    let opened = fixture.ptys.opened();
    assert_eq!(opened[0].program, std::path::PathBuf::from("/fake/shell"), "the platform's default shell");
    assert_eq!(opened[0].args, ["-c", "npm test"], "running the command, without the user's profile");
    assert_eq!(opened[0].cwd, fixture.folder);
    assert!(terminal.replay().contains("\r\n> npm test\r\n"), "the pane's screen shows the command first");
    assert!(terminal.is_running() && terminal.running_handles() == [handle.clone()]);

    assert!(terminal.write_to(&handle, "out\r\n"), "typed into the command");
    until(|| terminal.read_output(&handle, 0, 100).output.contains("out"), 5_000, "the command's output").await;
    assert_eq!(*seen.lock().unwrap(), "out\r\n", "the caller's listener gets the command's own output");
    let first = terminal.read(&handle).unwrap();
    assert_eq!((first.status, first.output.as_str(), first.more_remains), ("running", "\n> npm test\nout\n", false), "cleaned");
    assert_eq!(terminal.read(&handle).unwrap().output, "", "a read takes what it handed over");
    let cursor = terminal.read_output(&handle, 0, 1_000);
    assert!(cursor.output == "out\r\n" && !cursor.more_remains, "a reader with its own cursor is not moved by the agent's reads");
    let page = terminal.read_output(&handle, 0, 2);
    assert!(page.output == "ou" && page.more_remains && terminal.read_output(&handle, page.next_offset, 10).output == "t\r\n", "pages");

    fixture.ptys.exit(0, 3);
    assert_eq!(terminal.exited(&handle).await, Ok(Some(3)));
    let done = terminal.read(&handle).unwrap();
    assert!(done.status == "done" && done.exit_code == Some(3) && done.output.contains("[exit 3]"), "{done:?}");
    assert!(!terminal.write_to(&handle, "x") && !terminal.is_command_running(&handle), "an ended command takes no typing");
    assert_eq!(terminal.read("unknown"), Err(AgentTerminalError::UnknownHandle));
    terminal.dispose().await;
    assert_eq!(terminal.start("ls", None).await, Err(AgentTerminalError::Closed), "a closed terminal starts nothing");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_stop_interrupts_then_ends_the_command() {
    let fixture = Fixture::new("agent-stop");
    let terminal = fixture.agent();
    let handle = terminal.start("sleep", None).await.unwrap();
    let started = Instant::now();
    let stopped = terminal.stop(&handle).await.unwrap();
    assert!(stopped.status == "done" && stopped.exit_code == Some(KILLED), "{stopped:?}");
    assert!(started.elapsed() >= Duration::from_secs(3) && started.elapsed() < Duration::from_secs(10), "Ctrl+C, then 3 s, then ended");
    assert!(terminal.read_output(&handle, 0, 10).output.starts_with('\u{3}'), "Ctrl+C reached the command first");
    terminal.dispose().await;
}

#[tokio::test(flavor = "multi_thread")]
async fn a_pane_runs_eight_commands_at_once_and_says_why_a_start_failed() {
    let fixture = Fixture::new("agent-limits");
    let terminal = fixture.agent();
    let mut handles = vec![];
    for index in 0..MAXIMUM_RUNNING {
        handles.push(terminal.start(&format!("job {index}"), None).await.unwrap());
    }
    assert_eq!(terminal.start("one more", None).await, Err(AgentTerminalError::TooManyRunning));
    fixture.ptys.exit(0, 0);
    assert_eq!(terminal.exited(&handles[0]).await, Ok(Some(0)));
    fixture.ptys.fail_next_opens(1);
    let refused = terminal.start("refused", None).await;
    assert!(matches!(&refused, Err(AgentTerminalError::Start(reason)) if reason.contains("refused")), "{refused:?}");
    assert_eq!(terminal.running_handles().len(), MAXIMUM_RUNNING - 1, "a command that did not start is not running");
    until(|| terminal.replay().contains("[exit -1]"), 5_000, "the screen saying it ended").await;
    assert!(!terminal.write("typed"), "the newest command never started: the pane's keyboard has nowhere to type");
    let newest = terminal.start("newest", None).await.unwrap();
    assert!(terminal.write("typed"), "the pane's keyboard types into the newest command");
    until(|| terminal.read_output(&newest, 0, 10).output == "typed", 5_000, "the keyboard's text").await;
    terminal.resize(120, 30).unwrap();
    assert!(
        fixture.ptys.sizes().iter().filter(|s| s.columns == 120 && s.rows == 30).count() >= MAXIMUM_RUNNING - 1,
        "every running command"
    );
    terminal.dispose().await;
    assert!((0..MAXIMUM_RUNNING).all(|index| fixture.ptys.closed(index)), "the close ends every command");
}

/// The fake terminals, with the start of a command naming "slow" held until the check lets it go.
struct HeldStart {
    ptys: Arc<FakePtyHost>,
    release: tokio::sync::watch::Receiver<bool>,
}

impl PtyHost for HeldStart {
    fn default_shell(&self) -> PathBuf {
        self.ptys.default_shell()
    }
    fn shell_arguments(&self, shell: &Path, command: Option<&str>, no_profile: bool) -> Vec<String> {
        self.ptys.shell_arguments(shell, command, no_profile)
    }
    fn open(&self, request: PtyRequest) -> BoxFuture<'_, mighty_platform::Result<PtySession>> {
        Box::pin(async move {
            if request.args.iter().any(|a| a.contains("slow")) {
                let _ = self.release.clone().wait_for(|go| *go).await;
            }
            self.ptys.open(request).await
        })
    }
}

/// C# `AgentTerminal.DisposeAsync` closes every command at once: one still starting holds up none of the others.
#[tokio::test(flavor = "multi_thread")]
async fn a_command_still_starting_holds_up_no_other_close() {
    let fixture = Fixture::new("agent-dispose");
    let (go, release) = tokio::sync::watch::channel(false);
    let terminal =
        AgentTerminal::new(fixture.folder.clone(), Arc::new(HeldStart { ptys: fixture.ptys.clone(), release }), fixture.environment.clone());
    let starting = terminal.clone();
    let slow = tokio::spawn(async move { starting.start("slow start", None).await });
    until(|| terminal.replay().contains("> slow start"), 5_000, "the slow command begun").await;
    terminal.start("quick", None).await.unwrap();
    let closing = terminal.clone();
    let dispose = tokio::spawn(async move { closing.dispose().await });
    until(|| fixture.ptys.closed(0), 5_000, "the quick command closed while the slow one still starts").await;
    assert!(!dispose.is_finished(), "the close still waits for the command that is starting");
    go.send_replace(true);
    tokio::time::timeout(Duration::from_secs(10), dispose).await.expect("the close ends once it started").unwrap();
    let _ = slow.await;
    assert!(fixture.ptys.closed(1), "the slow command is closed too once it started");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_command_waiting_for_input_is_found_by_the_prompt_monitor() {
    let fixture = Fixture::new("agent-prompt");
    let terminal = fixture.agent();
    let handle = terminal.start("npx eas build", None).await.unwrap();
    assert!(terminal.write_to(&handle, "? Generate a new Android Keystore? › (Y/n)"));
    let start = Instant::now();
    let monitor = TerminalPromptMonitor::new(Duration::from_millis(100), Arc::new(move || start.elapsed()));
    let mut found = None;
    for _ in 0..100 {
        found = monitor.poll(terminal.as_ref());
        if found.is_some() {
            break;
        }
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
    let prompt = found.expect("the waiting command");
    assert!(prompt.kind == "yesNo" && prompt.handle == handle && prompt.default_yes == Some(true), "{prompt:?}");
    assert!(terminal.write_to(&prompt.handle, "y\r"), "the answer is typed into the command's terminal");
    monitor.answered(&prompt);
    terminal.dispose().await;
}

/// The AgentTerminal half of C# "Agent IO real ConPTY process ownership": Windows PowerShell run with the app's arguments writes
/// Unicode into the terminal even when this process's stdout is redirected and keeps its exit code; another pane's terminal does not
/// know the handle; a long command returns a handle to poll and is stopped (Ctrl+C, then its job ended) within the deadline.
#[cfg(windows)]
#[tokio::test(flavor = "multi_thread")]
async fn real_windows_interactive_process() {
    use mighty_terminal::TerminalTextCleaner;

    let directory = std::env::temp_dir().join(format!("mighty-agent-io-{}", std::process::id()));
    std::fs::create_dir_all(&directory).unwrap();
    let ptys = Arc::new(mighty_platform_windows::WindowsPtyHost::new());
    // The app's environment port: before any capture, the process's environment as the registry has it (no `=C:` names).
    let environment = Arc::new(mighty_platform_windows::WindowsShellEnvironment::new());
    let first = AgentTerminal::new(directory.clone(), ptys.clone(), environment.clone());
    let other = AgentTerminal::new(directory.clone(), ptys, environment);
    let mut result = first.run("[Console]::WriteLine(('mighty-'+'한글-marker')); exit 7").await.unwrap();
    for _ in 0..100 {
        if result.status != "running" {
            break;
        }
        tokio::time::sleep(Duration::from_millis(50)).await;
        result = first.read(&result.handle).unwrap();
    }
    assert_eq!(result.status, "done", "visible ConPTY command completes");
    assert_eq!(result.exit_code, Some(7), "visible ConPTY preserves exit code");
    let replay = TerminalTextCleaner::new().clean(&first.replay());
    assert!(
        replay.contains("mighty-한글-marker"),
        "visible ConPTY captures Unicode output even when the host stdout is redirected: {replay:?}"
    );
    assert_eq!(other.read(&result.handle), Err(AgentTerminalError::UnknownHandle), "handles never cross panes");
    let running = first.run("Start-Sleep -Seconds 300").await.unwrap();
    assert_eq!(running.status, "running", "long commands return a pollable handle");
    let timer = Instant::now();
    let stopped = first.stop(&running.handle).await.unwrap();
    assert!(
        stopped.status == "done" && timer.elapsed() < Duration::from_secs(10),
        "stop interrupts then kills only owned job within deadline"
    );
    first.dispose().await;
    other.dispose().await;
    for _ in 0..50 {
        if std::fs::remove_dir_all(&directory).is_ok() {
            break;
        }
        tokio::time::sleep(Duration::from_millis(100)).await;
    }
}
