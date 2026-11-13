//! C# `CliRunnerVerification` (MightyCode.Core.Tests/CliRunnerVerification.cs), against the stand-in child, plus the CliRunner
//! half of "the toolkit executor fails a step that cannot start" (WindowsCommandVerification.cs:69-79) as behaviour: a bare name
//! is found along the PATH and a `.cmd` shim runs through cmd.exe, and a program that cannot start is an error.
#![cfg(windows)]

mod common;

use std::collections::BTreeMap;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use common::*;
use mighty_platform::fake::FakeShellEnvironment;
use mighty_platform_windows::{CliRunner, WindowsProcessHost};

fn runner() -> CliRunner {
    let environment = FakeShellEnvironment::new(std::env::vars().collect());
    CliRunner::new(Arc::new(WindowsProcessHost::new()), Arc::new(environment))
}

fn args(values: &[&str]) -> Vec<String> {
    values.iter().map(|value| value.to_string()).collect()
}

/// C# "cli runner runs a real short command" (CliRunnerVerification.cs:11-25).
#[tokio::test(flavor = "multi_thread")]
async fn real_short_command() {
    let directory = temp("cli-short");
    let record = directory.join("record").to_string_lossy().into_owned();
    let result = runner()
        .run(&fake_text(), &args(&["--fake-cli", "claude", &record, "--version"]), Duration::from_secs(5), None, None)
        .await
        .unwrap();
    assert_eq!(result.exit_code, 0, "exit code must be 0");
    assert!(!result.timed_out, "short command must not time out");
    assert!(result.output.contains("2.1.271"), "output must contain version string, got: {}", result.output.trim());
    remove_when_free(&directory).await;
}

/// C# "cli runner kills a process that exceeds the timeout" (CliRunnerVerification.cs:29-44).
#[tokio::test(flavor = "multi_thread")]
async fn kills_command_that_exceeds_timeout() {
    let directory = temp("cli-timeout");
    let pid = directory.join("timeout.pid").to_string_lossy().into_owned();
    let started = Instant::now();
    let result = runner().run(&fake_text(), &args(&["--long-child", &pid]), Duration::from_secs(2), None, None).await.unwrap();
    assert!(result.timed_out, "timed-out flag must be true");
    assert_eq!(result.exit_code, -1);
    assert!(started.elapsed() < Duration::from_secs(15), "must finish well before the 60 s sleep, took {:?}", started.elapsed());
    assert!(result.output.contains("READY"), "what arrived before the kill is kept: {:?}", result.output);
    remove_when_free(&directory).await;
}

/// C# "cli runner reports the exit code and error output of a failing command" (CliRunnerVerification.cs:50-59).
#[tokio::test(flavor = "multi_thread")]
async fn reports_exit_code_and_error_output() {
    let result =
        runner().run(&fake_text(), &args(&["--fail-child", "installer said no"]), Duration::from_secs(10), None, None).await.unwrap();
    assert_eq!(result.exit_code, 3, "exit code must be the child's 3");
    assert!(!result.timed_out, "a command that exits on its own must not be reported as timed out");
    assert!(result.error_output.contains("installer said no"), "error output must be captured, got: {}", result.error_output.trim());
    assert!(result.output.trim().is_empty(), "standard output must stay empty, got: {}", result.output.trim());
}

/// C# "cli runner caps the output it captures" (CliRunnerVerification.cs:63-73): truncated, and the child still finishes instead
/// of blocking on a pipe nobody drains.
#[tokio::test(flavor = "multi_thread")]
async fn caps_the_captured_output() {
    let result = runner()
        .with_output_cap(2048)
        .run(&fake_text(), &args(&["--noisy-child", "262144"]), Duration::from_secs(20), None, None)
        .await
        .unwrap();
    let bytes = result.output.len();
    assert_eq!(result.exit_code, 0, "a capped command must still finish");
    assert!(!result.timed_out, "a capped command must not be reported as timed out");
    assert!(bytes > 0, "capped output must still hold the start of the stream");
    assert!(bytes <= 2048, "captured output must not exceed the cap, got {bytes} bytes");
}

/// C# "cli runner keeps arguments and output out of the log unredacted" (CliRunnerVerification.cs:77-88).
#[tokio::test(flavor = "multi_thread")]
async fn keeps_secrets_out_of_the_log() {
    const SECRET: &str = "sk-live-000-should-never-be-logged";
    let lines = Arc::new(Mutex::new(Vec::<String>::new()));
    let sink = lines.clone();
    let runner = runner().with_log(
        Arc::new(move |line: &str| sink.lock().unwrap().push(line.to_string())),
        Some(Arc::new(|text: &str| text.replace(SECRET, "[redacted]"))),
    );
    let result = runner.run(&fake_text(), &args(&["--fail-child", SECRET]), Duration::from_secs(10), None, None).await.unwrap();
    assert!(result.error_output.contains(SECRET), "the caller must still receive the unredacted output");
    let lines = lines.lock().unwrap();
    assert!(!lines.is_empty(), "the runner must log through the redaction point");
    assert!(lines.iter().all(|line| !line.contains(SECRET)), "no log line may contain the secret");
    assert!(lines.iter().any(|line| line.contains("[redacted]")), "the argument and the output must reach the log redacted");
    assert!(lines.iter().any(|line| line.starts_with("exit=3 timedOut=False outBytes=0 errBytes=")), "{lines:?}");
}

/// C# "cli runner kills the whole process group when a command times out" (CliRunnerVerification.cs:92-109).
#[tokio::test(flavor = "multi_thread")]
async fn timeout_kills_the_whole_process_group() {
    let directory = temp("cli-group");
    let pid_file = directory.join("group.pid");
    let pid = pid_file.to_string_lossy().into_owned();
    let fake = fake_text();
    let run = tokio::spawn(async move { runner().run(&fake, &args(&["--group-child", &pid]), Duration::from_secs(6), None, None).await });
    let grandchild = pid_from(&pid_file).await;
    assert!(alive(grandchild), "the fixture grandchild must be running before the time limit fires");
    let result = run.await.unwrap().unwrap();
    assert!(result.timed_out, "timed-out flag must be true");
    until(|| !alive(grandchild), 10_000, "the grandchild's end").await;
    remove_when_free(&directory).await;
}

/// C# "cli runner cancellation stops the command and its children" (CliRunnerVerification.cs:112-130): in Rust a cancelled run is
/// a dropped future (here an aborted task), which surfaces to the caller as the cancellation and ends the command and its children.
#[tokio::test(flavor = "multi_thread")]
async fn cancellation_stops_the_command_and_its_children() {
    let directory = temp("cli-cancel");
    let pid_file = directory.join("cancel.pid");
    let pid = pid_file.to_string_lossy().into_owned();
    let fake = fake_text();
    let run = tokio::spawn(async move { runner().run(&fake, &args(&["--group-child", &pid]), Duration::from_secs(300), None, None).await });
    let grandchild = pid_from(&pid_file).await;
    run.abort();
    assert!(run.await.unwrap_err().is_cancelled(), "cancellation must surface to the caller");
    until(|| !alive(grandchild), 10_000, "the grandchild's end").await;
    remove_when_free(&directory).await;
}

/// The CliRunner part of C# "the toolkit executor fails a step that cannot start": `CliRunner` finds a bare name along the PATH it
/// is given and runs a `.cmd` shim through cmd.exe; a name that is nowhere cannot start, and that is an error, not a result.
#[tokio::test(flavor = "multi_thread")]
async fn a_bare_name_is_found_along_the_path_and_one_that_cannot_start_is_an_error() {
    let directory = temp("cli-bare");
    let out = directory.join("args.txt");
    std::fs::write(directory.join("mightytool.cmd"), format!("@\"{}\" --print-args \"{}\" %*\r\n", fake_text(), out.to_string_lossy()))
        .unwrap();
    let mut environment: BTreeMap<String, String> = std::env::vars().collect();
    environment.retain(|name, _| !name.eq_ignore_ascii_case("PATH"));
    environment.insert("Path".into(), format!("relative;{}", directory.to_string_lossy()));
    let result =
        runner().run("mightytool", &args(&["a b", "c&d"]), Duration::from_secs(10), Some(&environment), Some(&directory)).await.unwrap();
    assert_eq!(result.exit_code, 0, "{result:?}");
    assert_eq!(std::fs::read_to_string(&out).unwrap(), "a b\0c&d");
    assert!(runner().run("no-such-mighty-tool", &[], Duration::from_secs(5), Some(&environment), None).await.is_err());
    remove_when_free(&directory).await;
}
