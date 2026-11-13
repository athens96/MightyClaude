//! `RunService::update_pane`, C#'s light updates of one pane (`DesktopService.SetShellStatus`, DesktopService.cs:165;
//! `UpdateShellBlockRun`, :219): ShellBlockRunVerification.TheServiceKeepsTheRecordWithThePane's service half — the pane's record
//! replaced without the whole state's normalize, nothing for a pane that is gone, the save each change asks for, and a full update
//! after it keeps it. The rules of the change itself (only an agent pane keeps shell block records, a shell pane takes its terminal's
//! status) are mighty-terminal's, checked there.

use std::collections::BTreeMap;
use std::sync::Arc;

use mighty_core::model::{AppSnapshot, RunSession, Workspace};
use mighty_platform::fake::{FakeProcessHost, FakeShellEnvironment};
use mighty_run::ServiceEvent;
use mighty_run::providers::ProviderCatalog;
use mighty_run::service::{PaneSave, RunService, RunServiceOptions};

fn service() -> (RunService, tokio::sync::mpsc::UnboundedReceiver<ServiceEvent>) {
    let workspace = Workspace { id: Some("w".into()), path: Some(std::env::temp_dir().display().to_string()), ..Workspace::default() };
    let pane = |id: &str, kind: &str| {
        Arc::new(RunSession { id: Some(id.into()), workspace_id: Some("w".into()), kind: Some(kind.into()), ..RunSession::default() })
    };
    let snapshot = AppSnapshot {
        workspaces: Some(vec![workspace]),
        sessions: Some(vec![pane("agent", "claude"), pane("shell", "shell")]),
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

#[tokio::test]
async fn the_service_replaces_one_pane_and_saves_as_asked() {
    let (service, mut events) = service();
    let status = |service: &RunService, id: &str| service.snapshot().session(id).map(|s| s.status().to_string());
    let changed = service.update_pane("shell", |pane| {
        let mut next = pane.clone();
        next.status = Some("running".into());
        Some((next, PaneSave::Later))
    });
    assert!(changed && status(&service, "shell").as_deref() == Some("running"), "the pane took the change");
    assert!(events.try_recv().is_err(), "a change saved later asks for no save");
    assert!(!service.update_pane("shell", |_| None), "no change");
    assert!(!service.update_pane("gone", |pane| Some((pane.clone(), PaneSave::Now))), "a pane that is gone");
    assert!(service.update_pane("agent", |pane| Some((pane.clone(), PaneSave::Soon))));
    assert!(matches!(events.try_recv(), Ok(ServiceEvent::SaveSoon)), "on the schedule");
    assert!(service.update_pane("shell", |pane| {
        let mut next = pane.clone();
        next.status = Some("completed".into());
        Some((next, PaneSave::Now))
    }));
    assert!(matches!(events.try_recv(), Ok(ServiceEvent::SaveNow)), "at once");
    service.update(|state| state.clone()).unwrap();
    assert_eq!(status(&service, "shell").as_deref(), Some("completed"), "a full update keeps it");
    service.dispose().await;
    assert!(!service.update_pane("shell", |pane| Some((pane.clone(), PaneSave::Now))), "a closing service changes nothing");
}
