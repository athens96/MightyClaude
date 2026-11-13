//! C# `PseudoTerminalVerification` (Windows-only in C#), the shell-activity checks of `WindowsParityVerification` (the real
//! reading is Windows-only in C#), and the ConPTY half of `AgentIOVerification.RealWindowsInteractiveProcess` (Windows-only in C#).
#![cfg(windows)]

mod common;

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use common::*;
use mighty_platform::{PtyHost, PtyRequest, PtySession, PtySize};
use mighty_platform_windows::shell_activity::{self, Entry, is_busy, next_status};
use mighty_platform_windows::{Variables, WindowsPtyHost};

fn cmd() -> PathBuf {
    PathBuf::from(std::env::var("SystemRoot").unwrap()).join("System32").join("cmd.exe")
}

/// The whole environment, as .NET reads this process's (C#'s terminals get `CliEnvironment.Current()`).
fn environment() -> std::collections::BTreeMap<String, String> {
    Variables::process().into_map()
}

async fn open(program: PathBuf, args: Vec<String>, cwd: &Path) -> (PtySession, Arc<Mutex<Vec<u8>>>) {
    let request = PtyRequest { program, args, cwd: cwd.to_path_buf(), env: environment(), size: PtySize::default() };
    let mut session = WindowsPtyHost::new().open(request).await.unwrap();
    let output = Arc::new(Mutex::new(Vec::new()));
    let sink = output.clone();
    let mut receiver = std::mem::replace(&mut session.output, tokio::sync::mpsc::channel(1).1);
    tokio::spawn(async move {
        while let Some(bytes) = receiver.recv().await {
            sink.lock().unwrap().extend_from_slice(&bytes);
        }
    });
    (session, output)
}

fn text(output: &Arc<Mutex<Vec<u8>>>) -> String {
    String::from_utf8_lossy(&output.lock().unwrap()).into_owned()
}

/// The text without its VT sequences (C# `TerminalTextCleaner`, enough for these checks): CSI and OSC sequences and lone escapes.
fn clean(text: &str) -> String {
    let mut out = String::new();
    let mut chars = text.chars().peekable();
    while let Some(c) = chars.next() {
        if c != '\x1b' {
            out.push(c);
            continue;
        }
        match chars.next() {
            Some('[') => {
                for c in chars.by_ref() {
                    if ('@'..='~').contains(&c) {
                        break;
                    }
                }
            }
            Some(']') => {
                while let Some(c) = chars.next() {
                    if c == '\x07' || (c == '\x1b' && chars.peek() == Some(&'\\')) {
                        chars.next_if_eq(&'\\');
                        break;
                    }
                }
            }
            _ => {}
        }
    }
    out
}

/// C# "ConPTY persistent shell, resize and deterministic shutdown" (PseudoTerminalVerification.cs:6-33).
#[tokio::test(flavor = "multi_thread")]
async fn persistent_shell_and_shutdown() {
    let path = temp("conpty");
    let (session, output) = open(cmd(), vec!["/d".into(), "/q".into()], &path).await;
    let terminal = session.control.clone();
    let marker = format!("MIGHTY_{:032x}", std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos());
    assert!(terminal.write(format!("set MIGHTY_TERMINAL_VALUE={}\r", &marker[..15]).as_bytes()), "First input was rejected.");
    terminal.write(format!("set MIGHTY_TERMINAL_VALUE=%MIGHTY_TERMINAL_VALUE%{}\r", &marker[15..]).as_bytes());
    assert!(terminal.write(b"echo %MIGHTY_TERMINAL_VALUE%\r"), "Second input was rejected.");
    until(|| text(&output).contains(&marker), 15_000, "the marker in the output").await;
    terminal.resize(PtySize { columns: 80, rows: 24 }).unwrap();
    assert!(!terminal.write(&vec![b'x'; 65_537]), "Oversized input was accepted.");
    terminal.write(b"exit /b 7\r");
    let code = tokio::time::timeout(Duration::from_secs(15), terminal.wait()).await.expect("the shell's exit").unwrap();
    assert_eq!(code, 7, "Shell exit code was lost.");
    tokio::time::timeout(Duration::from_secs(15), terminal.close()).await.expect("the close in 15 s").unwrap();
    assert!(!terminal.write(b"echo after-close\r"), "Closed terminal accepted input.");
    terminal.close().await.unwrap();
    assert!(terminal.process_ids().is_none());
    terminal.resize(PtySize { columns: 10, rows: 10 }).unwrap();
    remove_when_free(&path).await;
}

/// C# "windows parity: a shell pane's status follows its shell" (WindowsParityVerification.cs:23-38), its ShellActivity part (the
/// DesktopService part belongs to the core's port).
#[test]
fn shell_pane_status_follows_its_shell() {
    const SHELL: u32 = 400;
    const CONSOLE: i64 = 4001;
    let process = |id: u32, on: i64| Entry { id, console: on };
    assert!(!is_busy(SHELL, &[process(SHELL, CONSOLE)]), "a shell alone in its job is at its prompt");
    assert!(is_busy(SHELL, &[process(SHELL, CONSOLE), process(12, CONSOLE)]), "a process on the shell's console is a command running");
    assert!(
        !is_busy(SHELL, &[process(SHELL, CONSOLE), process(12, i64::from(SHELL))]),
        "a GUI program the shell started is on no console: the shell is at its prompt"
    );
    assert!(
        !is_busy(SHELL, &[process(SHELL, CONSOLE), process(16, 5001), process(20, 5001)]),
        "a program in a console window of its own, and what runs in that window, are not on the terminal"
    );
    assert!(!is_busy(SHELL, &[process(SHELL, CONSOLE), process(24, 0)]), "a process whose console could not be read is not counted");
    assert!(
        !is_busy(SHELL, &[process(SHELL, 0), process(12, 0)]) && !is_busy(SHELL, &[process(12, CONSOLE)]),
        "with the shell's console unknown nothing is running"
    );
    assert!(
        is_busy(SHELL, &[process(SHELL, CONSOLE), process(16, 5001), process(28, CONSOLE)]),
        "a command on the terminal beside a windowed program is running"
    );
    assert!(
        next_status("idle", Some(true)) == Some("running") && next_status("running", Some(false)) == Some("idle"),
        "a reading moves the pane between idle and running"
    );
    assert!(
        next_status("idle", Some(false)).is_none() && next_status("running", Some(true)).is_none(),
        "only a different status is reported"
    );
    assert!(
        next_status("idle", None).is_none() && next_status("running", None).is_none(),
        "a reading that could not be taken changes nothing"
    );
    assert!(
        next_status("completed", Some(true)).is_none() && next_status("error", Some(false)).is_none(),
        "a pane that has exited or failed keeps that"
    );
}

/// C# "windows parity: a real shell reads running only while a command runs" (WindowsParityVerification.cs:81-107): a cmd shell
/// in ConPTY is idle at its prompt, running while ping runs under it, and idle again after; a GUI program it starts (wscript //B)
/// runs in the job but on no console, so the shell is at its prompt.
#[tokio::test(flavor = "multi_thread")]
async fn real_shell_reads_running_only_while_a_command_runs() {
    let path = temp("activity");
    let (session, _output) = open(cmd(), vec!["/d".into(), "/q".into()], &path).await;
    let (terminal, shell) = (session.control.clone(), session.id);
    let busy = || shell_activity::probe(terminal.as_ref(), shell);
    assert!(
        terminal.process_ids().is_some_and(|job| job.contains(&shell)) && shell_activity::console(shell).is_some(),
        "the shell's job must hold the shell, and its console must be readable"
    );
    until(|| busy() == Some(false), 15_000, "idle at the prompt").await;
    assert!(terminal.write(b"ping -n 6 127.0.0.1 >nul\r"), "The command was rejected.");
    until(|| busy() == Some(true), 15_000, "running while ping runs").await;
    until(|| busy() == Some(false), 30_000, "idle after ping").await;
    std::fs::write(path.join("wait.js"), "WScript.Sleep(8000);").unwrap();
    assert!(terminal.write(b"wscript //B //Nologo wait.js\r"), "The GUI program was rejected.");
    until(|| terminal.process_ids().is_some_and(|job| job.len() > 1), 15_000, "wscript in the job").await;
    assert_eq!(busy(), Some(false), "a GUI program started from the shell must leave its pane idle");
    tokio::time::timeout(Duration::from_secs(15), terminal.close()).await.expect("the close in 15 s").unwrap();
    assert_eq!(busy(), None, "a closed terminal gives no reading");
    // The GUI program can outlive the pane for a moment and still hold the folder.
    remove_when_free(&path).await;
}

/// The ConPTY half of C# "Agent IO real ConPTY process ownership" (AgentIOVerification.cs:75-94): Windows PowerShell run with the
/// app's arguments (`ShellArguments(shell, text, noProfile: true)`, as AgentTerminal.cs:72-74) writes Unicode into the terminal
/// even when this process's stdout is redirected, keeps its exit code, and a long command is stopped within the deadline. The
/// AgentTerminal handles and their ownership belong to the Agent IO stage.
#[tokio::test(flavor = "multi_thread")]
async fn real_windows_interactive_process() {
    let host = WindowsPtyHost::new();
    let directory = temp("agent-io");
    let shell = host.default_shell();
    let arguments = host.shell_arguments(&shell, Some("[Console]::WriteLine(('mighty-'+'한글-marker')); exit 7"), true);
    let (session, output) = open(shell.clone(), arguments, &directory).await;
    let code = tokio::time::timeout(Duration::from_secs(30), session.control.wait()).await.expect("the command's end").unwrap();
    assert_eq!(code, 7, "visible ConPTY preserves exit code");
    tokio::time::timeout(Duration::from_secs(15), session.control.close()).await.unwrap().unwrap();
    let replay = clean(&text(&output));
    assert!(
        replay.contains("mighty-한글-marker"),
        "visible ConPTY captures Unicode output even when the host stdout is redirected: {replay:?}"
    );

    let arguments = host.shell_arguments(&shell, Some("Start-Sleep -Seconds 300"), true);
    let (running, _output) = open(shell, arguments, &directory).await;
    tokio::time::sleep(Duration::from_millis(500)).await;
    assert!(running.control.process_ids().is_some_and(|ids| ids.contains(&running.id)), "long commands are running");
    let started = Instant::now();
    tokio::time::timeout(Duration::from_secs(10), running.control.close()).await.expect("stop within the deadline").unwrap();
    assert!(started.elapsed() < Duration::from_secs(10), "stop kills the owned job within deadline");
    assert!(!alive(running.id));
    remove_when_free(&directory).await;
}

/// `PseudoTerminal.DefaultShell` and `ShellArguments` (PseudoTerminal.cs:49-61): Windows PowerShell 5.1 on this system, UTF-8 set
/// for that shell only; cmd.exe switched to code page 65001.
#[test]
fn the_default_shell_and_its_arguments() {
    let host = WindowsPtyHost::new();
    let shell = host.default_shell();
    assert!(shell.ends_with(r"WindowsPowerShell\v1.0\powershell.exe"), "{shell:?}");
    let utf8 = mighty_platform_windows::pty::POWERSHELL_UTF8;
    assert_eq!(host.shell_arguments(&shell, None, false), ["-NoLogo", "-NoExit", "-Command", utf8]);
    assert_eq!(host.shell_arguments(&shell, Some("ls"), true), ["-NoLogo", "-NoProfile", "-Command", &format!("{utf8}ls")]);
    assert_eq!(host.shell_arguments(&cmd(), None, false), ["/d", "/k", "chcp 65001 >nul"]);
    assert_eq!(host.shell_arguments(&cmd(), Some("dir"), true), ["/d", "/s", "/c", "chcp 65001 >nul & dir"]);
}

/// A terminal refuses a folder that is not there, a shell that is not there, and an environment Windows cannot hold.
#[tokio::test]
async fn a_terminal_refuses_what_it_cannot_start() {
    let host = WindowsPtyHost::new();
    let directory = temp("pty-refuse");
    let request = PtyRequest { program: cmd(), args: vec![], cwd: directory.join("missing"), env: environment(), size: PtySize::default() };
    assert!(host.open(request.clone()).await.is_err());
    let missing_shell = PtyRequest { program: directory.join("no-shell.exe"), cwd: directory.clone(), ..request.clone() };
    assert!(host.open(missing_shell).await.is_err());
    let mut env = environment();
    env.insert("A=B".into(), "x".into());
    assert!(host.open(PtyRequest { env, cwd: directory.clone(), ..request }).await.is_err());
    remove_when_free(&directory).await;
}
