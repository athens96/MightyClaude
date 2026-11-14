//! The service half of C# "windows parity: a shell pane's status follows its shell" (WindowsParityVerification.cs:40-78) and of
//! ShellBlockRunVerification.TheServiceKeepsTheRecordWithThePane: a shell pane's status from its terminal (`SetShellStatus`) and a
//! shell block's record (`UpdateShellBlockRun`) as light updates through the run service — never a run event, saved only when C#
//! saves them, read back as C# reads them after a restart. The ShellActivity half (the job reading, `NextStatus`) is
//! mighty-platform-windows' `shell_pane_status_follows_its_shell`; `next_status` is checked here too.

use std::collections::BTreeMap;
use std::sync::Arc;

use mighty_core::model::{AppSnapshot, RunSession, ShellBlockRun, Workspace};
use mighty_core::normalize::normalize;
use mighty_platform::fake::{FakeProcessHost, FakeShellEnvironment};
use mighty_run::ServiceEvent;
use mighty_run::providers::ProviderCatalog;
use mighty_run::service::{PaneSave, RunService, RunServiceOptions};
use mighty_terminal::activity::{activity_change, next_status, shell_status_change};
use mighty_terminal::shell_blocks::{self, OUTPUT_LIMIT_BYTES};
use tokio::sync::mpsc::UnboundedReceiver;

fn service() -> (RunService, UnboundedReceiver<ServiceEvent>) {
    let workspace = Workspace { id: Some("w".into()), path: Some(std::env::temp_dir().display().to_string()), ..Workspace::default() };
    let pane = |id: &str, kind: &str| {
        Arc::new(RunSession { id: Some(id.into()), workspace_id: Some("w".into()), kind: Some(kind.into()), ..RunSession::default() })
    };
    let snapshot = AppSnapshot {
        workspaces: Some(vec![workspace]),
        sessions: Some(vec![pane("shell", "shell"), pane("agent", "claude")]),
        ..AppSnapshot::default()
    };
    let catalog =
        Arc::new(ProviderCatalog::new(Arc::new(FakeProcessHost::default()), Arc::new(FakeShellEnvironment::new(BTreeMap::new())), None));
    RunService::new(
        snapshot,
        catalog,
        RunServiceOptions { plugin_directory: String::new(), permission_card: false, remote: None, configure: None, admitting: None },
    )
}

/// C# `SetShellStatus` as the host does it (terminal.rs `set_shell_status`).
fn set_shell_status(service: &RunService, id: &str, status: &str) -> bool {
    service.update_pane(id, |pane| {
        shell_status_change(pane, status).map(|(next, now)| (next, if now { PaneSave::Now } else { PaneSave::Later }))
    })
}

fn status(service: &RunService, id: &str) -> Option<String> {
    service.snapshot().session(id).map(|s| s.status().to_string())
}

/// Every event the service sent so far.
fn drain(events: &mut UnboundedReceiver<ServiceEvent>) -> Vec<ServiceEvent> {
    std::iter::from_fn(|| events.try_recv().ok()).collect()
}

#[tokio::test]
async fn shell_pane_status_follows_its_shell() {
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

    let (service, mut events) = service();
    assert!(
        status(&service, "shell").as_deref() == Some("idle") && !set_shell_status(&service, "shell", "idle"),
        "the status a pane already has is no change"
    );
    assert!(
        set_shell_status(&service, "shell", "running") && status(&service, "shell").as_deref() == Some("running"),
        "a command running sets the pane running"
    );
    assert!(drain(&mut events).is_empty(), "idle and running are not saved: a restart reads a running shell as idle anyway");
    assert!(
        set_shell_status(&service, "shell", "completed")
            && set_shell_status(&service, "shell", "error")
            && set_shell_status(&service, "shell", "idle"),
        "exit, failure and restart are written"
    );
    let saves = drain(&mut events);
    assert!(saves.len() == 2 && saves.iter().all(|e| matches!(e, ServiceEvent::SaveNow)), "an exit or a failure is saved: {saves:?}");
    assert!(
        !set_shell_status(&service, "agent", "running") && status(&service, "agent").as_deref() == Some("idle"),
        "only a shell pane takes a terminal's status"
    );
    assert!(
        !set_shell_status(&service, "shell", "stopped") && !set_shell_status(&service, "missing", "running"),
        "an unknown status or pane changes nothing"
    );
    assert!(drain(&mut events).iter().all(|e| !matches!(e, ServiceEvent::Run { .. })), "a shell pane's status is not a run event");
    set_shell_status(&service, "shell", "running");
    // A shell that was running a command when the app closed is at a new prompt next time (Swift StateStore.swift:201).
    let restored = normalize(&service.snapshot(), true).unwrap();
    assert_eq!(restored.session("shell").map(|s| s.status()), Some("idle"), "a shell pane saved as running must be idle after a restart");
    service.dispose().await;
}

/// A reading taken before the shell exited lands after the exit's "completed": it is decided against the status the pane has when
/// the change is made (inside the service's update), so the pane keeps completed (and error) instead of going back to idle or running.
#[tokio::test]
async fn a_late_activity_reading_never_overwrites_an_exit() {
    let (service, _events) = service();
    let read = |busy: bool| service.update_pane("shell", |pane| activity_change(pane, busy).map(|(next, _)| (next, PaneSave::Later)));
    assert!(read(true) && status(&service, "shell").as_deref() == Some("running"), "a command running");
    assert!(!read(true), "the same reading again changes nothing");
    for end in ["completed", "error"] {
        assert!(set_shell_status(&service, "shell", end));
        assert!(!read(false) && !read(true), "a reading after the {end} changes nothing");
        assert_eq!(status(&service, "shell").as_deref(), Some(end));
    }
    assert!(!service.update_pane("agent", |pane| activity_change(pane, true).map(|(next, _)| (next, PaneSave::Later))), "a shell's only");
    service.dispose().await;
}

/// C# "shell block runs: the service keeps the record with its pane across a relaunch".
#[tokio::test]
async fn the_service_keeps_the_record_with_the_pane() {
    let (service, mut events) = service();
    let update = |id: &str, change: &dyn Fn(Option<&ShellBlockRun>) -> Option<ShellBlockRun>| {
        service.update_pane(id, |pane| {
            shell_blocks::update_pane(pane, "e#0", change).map(|(next, ended)| (next, if ended { PaneSave::Now } else { PaneSave::Soon }))
        })
    };
    assert!(update("agent", &|_| Some(shell_blocks::started("e#0", "ping localhost", None))), "started");
    assert!(!update("shell", &|_| Some(shell_blocks::started("e#0", "ls", None))), "a shell pane keeps none");
    assert!(!update("agent", &|run| run.cloned()), "the same record changes nothing");
    assert!(update("agent", &|run| Some(ShellBlockRun { output: Some("x".repeat(40_000)), ..run.cloned().unwrap() })), "output");
    assert!(drain(&mut events).iter().all(|e| matches!(e, ServiceEvent::SaveSoon)), "a running record is saved on the schedule");
    let saved = service.snapshot().session("agent").unwrap().shell_block_runs.clone().unwrap();
    assert!(
        saved[0].state.as_deref() == Some("running") && saved[0].output.as_ref().unwrap().len() == OUTPUT_LIMIT_BYTES,
        "held to the limit"
    );
    service.update(|state| state.clone()).unwrap();
    assert_eq!(
        service.snapshot().session("agent").unwrap().shell_block_runs.as_ref().unwrap()[0].state.as_deref(),
        Some("running"),
        "a full update keeps it running"
    );
    let restored = normalize(&service.snapshot(), true).unwrap();
    let runs = restored.session("agent").unwrap().shell_block_runs.clone().unwrap();
    assert!(
        runs[0].key.as_deref() == Some("e#0") && runs[0].state.as_deref() == Some("interrupted"),
        "after the quit it reads interrupted"
    );
    service.dispose().await;
}
