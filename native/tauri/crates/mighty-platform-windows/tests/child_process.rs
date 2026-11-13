//! C# `ChildProcess` on Windows (Verification.cs:1597-1646, the Windows-only "Job Object / UTF-8 console" check) and the shared
//! `capture()` helper on the Windows host: what the fake's contract (mighty-platform tests/ports.rs) states, by real processes.
#![cfg(windows)]

mod common;

use std::time::Duration;

use common::*;
use mighty_platform::{PlatformError, ProcessHost, SpawnRequest, capture};
use mighty_platform_windows::{KILLED_EXIT_CODE, WindowsProcessHost};
use tokio::io::AsyncWriteExt;

fn comspec() -> String {
    std::env::var("ComSpec").expect("ComSpec")
}

/// C# "Windows Job Object handles Unicode and kills descendants after parent exits" (Verification.cs:1599-1644): cmd's `echo`
/// writes UTF-8 into the pipe through the app's console at code page 65001; a descendant `start /b` left running after its parent
/// exited is still alive until the child is closed, and dead when the close returns.
#[tokio::test(flavor = "multi_thread")]
async fn windows_job_object_handles_unicode_and_kills_descendants_after_parent_exits() {
    let host = WindowsProcessHost::new();
    let directory = temp("job");
    let pid = directory.join("descendant.pid");

    // Unicode output.
    let command = "echo 안녕하세요";
    let mut request = SpawnRequest::new(comspec(), ["/d", "/s", "/c", command], &directory);
    request.shell_command = Some(command.into());
    let mut child = host.spawn(request).await.unwrap();
    drop(child.stdin.take());
    let output = tokio::time::timeout(Duration::from_secs(5), read_all(child.stdout.take())).await.expect("output in time");
    assert_eq!(tokio::time::timeout(Duration::from_secs(5), child.wait()).await.unwrap().unwrap(), 0, "Unicode shell failed");
    let output = String::from_utf8_lossy(&output);
    assert!(output.contains("안녕하세요"), "Unicode shell output changed: {output:?}");
    child.close().await.unwrap();

    // Descendant startup, parent exit, job disposal.
    let start = format!("start \"\" /b {}", long_command(&pid));
    let mut request = SpawnRequest::new(comspec(), ["/d", "/s", "/c", start.as_str()], &directory);
    request.shell_command = Some(start.clone());
    let mut tree = host.spawn(request).await.unwrap();
    drop(tree.stdin.take());
    let descendant = pid_from(&pid).await;
    assert_eq!(tokio::time::timeout(Duration::from_secs(5), tree.wait()).await.unwrap().unwrap(), 0, "Parent shell failed");
    assert!(alive(descendant), "The fixture descendant must outlive its parent before job disposal");
    tree.close().await.unwrap();
    assert!(!alive(descendant), "Job disposal returned while its descendant was alive");
    remove_when_free(&directory).await;
}

/// `env_remove` takes names out of the app's environment before `env` is laid over it, ignoring case as Windows does
/// (`WorkspaceGitInfo.Command`'s `info.Environment.Remove` of every `GIT_*` name, then its own three): a removed name the request
/// sets again has the request's value; `cli_request` and `shell_request` start a program and cmd.exe as C# does.
#[tokio::test(flavor = "multi_thread")]
async fn a_removed_variable_is_not_inherited_and_one_set_again_has_its_new_value() {
    let host = WindowsProcessHost::new();
    let directory = temp("env-remove");
    assert!(std::env::var_os("PATH").is_some() && std::env::var_os("ComSpec").is_some(), "the test reads two inherited names");
    let file = directory.join("env.txt");
    let request =
        fake_request(&["--print-env", &file.to_string_lossy()], &directory).without("path").without("COMSPEC").env("ComSpec", "again");
    let child = host.spawn(request).await.unwrap();
    assert_eq!(tokio::time::timeout(Duration::from_secs(10), child.wait()).await.unwrap().unwrap(), 0);
    child.close().await.unwrap();
    let text = std::fs::read_to_string(&file).unwrap();
    let entries: Vec<&str> = text.split('\0').collect();
    assert!(!entries.iter().any(|entry| entry.to_ascii_uppercase().starts_with("PATH=")), "a removed name is gone, whatever its case");
    assert!(entries.iter().any(|entry| entry.eq_ignore_ascii_case("COMSPEC=again")), "a name set again has its new value");
    assert!(entries.contains(&"NO_COLOR=1"));
    assert!(host.spawn(fake_request(&["--echo"], &directory).without("")).await.is_err(), "an empty name is refused");

    let cli = host.cli_request(&fake(), &["--echo".to_string()], &directory, &Default::default()).unwrap();
    assert!(cli.shell_command.is_none() && cli.program == fake() && cli.env.get("NO_COLOR").map(String::as_str) == Some("1"));
    let shim = host
        .cli_request(std::path::Path::new(r"C:\npm\pi.cmd"), &["--mode".to_string(), "rpc".to_string()], &directory, &Default::default())
        .unwrap();
    assert_eq!(
        shim.shell_command.as_deref(),
        Some("\"C:\\npm\\pi.cmd\" \"--mode\" \"rpc\""),
        "a shim runs through cmd.exe, each argument quoted"
    );
    assert_eq!(shim.env.get("NoDefaultCurrentDirectoryInExePath").map(String::as_str), Some("1"));
    let shell = host.shell_request("echo 안녕", &directory, &Default::default());
    let mut shell_child = host.spawn(shell).await.unwrap();
    drop(shell_child.stdin.take());
    let output = tokio::time::timeout(Duration::from_secs(5), read_all(shell_child.stdout.take())).await.expect("output in time");
    assert!(String::from_utf8_lossy(&output).contains("안녕"));
    shell_child.close().await.unwrap();
    remove_when_free(&directory).await;
}

/// The grandchild dies with the job: a `--group-child` whose own child was started with no job of its own (std's spawn) is
/// killed, grandchild and all, by `kill`, and a close after it finds the job empty.
#[tokio::test(flavor = "multi_thread")]
async fn a_grandchild_dies_when_the_parents_job_closes() {
    let host = WindowsProcessHost::new();
    let directory = temp("grandchild");
    let pid = directory.join("group.pid");
    let child = host.spawn(fake_request(&["--group-child", &pid.to_string_lossy()], &directory)).await.unwrap();
    let grandchild = pid_from(&pid).await;
    let parent = child.id;
    assert!(alive(parent) && alive(grandchild));
    child.close().await.unwrap();
    assert!(!alive(grandchild), "closing the job must end the grandchild before close returns");
    assert!(!alive(parent));
    remove_when_free(&directory).await;
}

#[tokio::test(flavor = "multi_thread")]
async fn a_killed_child_reports_the_job_exit_code_and_a_second_close_does_nothing() {
    let host = WindowsProcessHost::new();
    let directory = temp("kill");
    let pid = directory.join("long.pid");
    let child = host.spawn(fake_request(&["--long-child", &pid.to_string_lossy()], &directory)).await.unwrap();
    let id = pid_from(&pid).await;
    assert_eq!(id, child.id, "Child::id is the process id");
    let control = child.control();
    child.kill();
    assert_eq!(control.wait().await.unwrap(), KILLED_EXIT_CODE);
    control.close().await.unwrap();
    control.close().await.unwrap();
    control.kill();
    drop(child);
    remove_when_free(&directory).await;
}

/// The environment C# `ChildProcess.StartInfo` gives: this process's with the request's laid over it, a name in another case
/// being the same variable (it keeps the parent's spelling), an empty value set and empty, `NO_COLOR=1`.
#[tokio::test(flavor = "multi_thread")]
async fn the_child_environment_is_the_apps_with_the_request_laid_over_it() {
    let host = WindowsProcessHost::new();
    let directory = temp("env");
    let file = directory.join("env.txt");
    let request = fake_request(&["--print-env", &file.to_string_lossy()], &directory)
        .env("path", r"C:\override")
        .env("MIGHTY_EMPTY", "")
        .env("MIGHTY_VALUE", "한글 = value");
    let child = host.spawn(request).await.unwrap();
    assert_eq!(child.wait().await.unwrap(), 0);
    child.close().await.unwrap();
    let text = std::fs::read_to_string(&file).unwrap();
    let entries: Vec<(&str, &str)> = text.split('\0').filter_map(|entry| entry.split_once('=')).collect();
    let paths: Vec<_> = entries.iter().filter(|(name, _)| name.eq_ignore_ascii_case("PATH")).collect();
    assert_eq!(paths.len(), 1, "one PATH: {paths:?}");
    assert_eq!(paths[0].1, r"C:\override");
    assert!(entries.contains(&("MIGHTY_EMPTY", "")), "an empty value stays set");
    assert!(entries.contains(&("MIGHTY_VALUE", "한글 = value")));
    assert!(entries.contains(&("NO_COLOR", "1")));
    assert!(entries.iter().any(|(name, _)| name.eq_ignore_ascii_case("SystemRoot")), "the app's own environment is inherited");
    remove_when_free(&directory).await;
}

#[tokio::test(flavor = "multi_thread")]
async fn stdin_reaches_the_child_and_its_shutdown_is_the_end_of_input() {
    let host = WindowsProcessHost::new();
    let directory = temp("echo");
    let mut child = host.spawn(fake_request(&["--echo"], &directory)).await.unwrap();
    let mut input = child.stdin.take().unwrap();
    input.write_all("hello 한글\n".as_bytes()).await.unwrap();
    input.shutdown().await.unwrap();
    assert!(input.write_all(b"late").await.is_err(), "a closed stdin takes nothing more");
    let output = tokio::time::timeout(Duration::from_secs(10), read_all(child.stdout.take())).await.unwrap();
    assert_eq!(output, "hello 한글\n".as_bytes());
    assert_eq!(child.wait().await.unwrap(), 0);
    child.close().await.unwrap();
    remove_when_free(&directory).await;
}

#[tokio::test(flavor = "multi_thread")]
async fn relative_paths_and_nul_bytes_are_refused() {
    let host = WindowsProcessHost::new();
    let directory = temp("refuse");
    assert!(matches!(host.spawn(SpawnRequest::new("cmd.exe", ["/c"], &directory)).await, Err(PlatformError::Invalid(_))));
    assert!(matches!(host.spawn(SpawnRequest::new(fake(), ["--echo"], "relative")).await, Err(PlatformError::Invalid(_))));
    assert!(matches!(host.spawn(fake_request(&["a\0b"], &directory)).await, Err(PlatformError::Invalid(_))));
    assert!(matches!(host.spawn(fake_request(&["--echo"], &directory).env("A=B", "x")).await, Err(PlatformError::Invalid(_))));
    let missing = directory.join("missing.exe");
    assert!(matches!(host.spawn(SpawnRequest::new(missing, ["x"], &directory)).await, Err(PlatformError::Io(_))));
    remove_when_free(&directory).await;
}

// ---- the shared capture() on the Windows host -------------------------------------------------------------------------------

#[tokio::test(flavor = "multi_thread")]
async fn a_capture_feeds_stdin_and_collects_both_outputs() {
    let host = WindowsProcessHost::new();
    let directory = temp("capture");
    let captured =
        capture(&host, fake_request(&["--echo"], &directory), Some(b"input".to_vec()), Duration::from_secs(10), 1024).await.unwrap();
    assert_eq!((captured.exit_code, captured.stdout.as_slice(), captured.stderr.as_slice()), (0, &b"input"[..], &b""[..]));
    let failed = capture(&host, fake_request(&["--fail-child", "said no"], &directory), None, Duration::from_secs(10), 1024).await.unwrap();
    assert_eq!(failed.exit_code, 3);
    assert_eq!(String::from_utf8_lossy(&failed.stderr).trim(), "said no");
    remove_when_free(&directory).await;
}

/// Past its timeout a capture kills the child and everything it started (the grandchild is dead when the capture returns).
#[tokio::test(flavor = "multi_thread")]
async fn a_capture_kills_a_child_past_its_timeout_with_its_descendants() {
    let host = WindowsProcessHost::new();
    let directory = temp("capture-timeout");
    let pid = directory.join("group.pid");
    let request = fake_request(&["--group-child", &pid.to_string_lossy()], &directory);
    let error = capture(&host, request, None, Duration::from_secs(5), 1024).await.unwrap_err();
    assert!(matches!(error, PlatformError::TimedOut(_)), "{error}");
    let grandchild: u32 = std::fs::read_to_string(&pid).unwrap().trim().parse().unwrap();
    assert!(!alive(grandchild), "the capture's close must have ended the grandchild");
    remove_when_free(&directory).await;
}

#[tokio::test(flavor = "multi_thread")]
async fn a_capture_past_its_output_limit_fails_and_ends_the_child() {
    let host = WindowsProcessHost::new();
    let directory = temp("capture-limit");
    let error =
        capture(&host, fake_request(&["--noisy-child", "262144"], &directory), None, Duration::from_secs(20), 1024).await.unwrap_err();
    assert!(matches!(error, PlatformError::TooLarge(1024)), "{error}");
    remove_when_free(&directory).await;
}
