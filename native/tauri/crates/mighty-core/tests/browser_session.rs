//! C# `BrowserSessionVerification.BrowserSessionLoadsWithWorkspaceProfileKey` and `BrowserVerification.SessionFieldsShareMacOSNames`:
//! a browser pane saved by the Mac loads here with its workspace's profile key, under the Mac's field names.

mod common;

use mighty_core::Wire;
use mighty_core::locale::Locale;
use mighty_core::model::RunSession;
use mighty_core::store::Store;
use serde_json::{Value, json};

#[test]
fn browser_session_fields_share_the_mac_names() {
    let session = RunSession::read(&json!({
        "id": "s1", "workspaceId": "ws1", "kind": "browser", "workspaceProfileKey": "ws1", "ownerSessionId": "owner-1",
    }))
    .unwrap();
    let written: Value = serde_json::from_str(&session.to_json()).unwrap();
    assert_eq!(written["workspaceProfileKey"], "ws1");
    assert_eq!(written["ownerSessionId"], "owner-1");
    let again = RunSession::read(&written).unwrap();
    assert_eq!((again.workspace_profile_key.as_deref(), again.owner_session_id.as_deref()), (Some("ws1"), Some("owner-1")));
    let without = RunSession::read(&json!({"id": "s2", "workspaceId": "ws1", "kind": "browser", "workspaceProfileKey": "ws1"})).unwrap();
    let written: Value = serde_json::from_str(&without.to_json()).unwrap();
    assert!(written.get("ownerSessionId").is_none(), "absent when null");
}

#[test]
fn a_browser_session_loads_with_its_workspace_profile_key() {
    let folder = common::temp_dir("browser-session");
    let workspace = folder.to_string_lossy().into_owned();
    let state = json!({
        "version": 1,
        "workspaces": [{"id": "5a0e0b0b-0000-4000-8000-0000000000a1", "name": "w", "path": workspace}],
        "sessions": [
            {"id": "browser-session-1", "workspaceId": "5a0e0b0b-0000-4000-8000-0000000000a1", "kind": "browser",
             "workspaceProfileKey": "5a0e0b0b-0000-4000-8000-0000000000a1", "status": "running",
             "logs": [{"id": "l1", "kind": "user", "text": "x", "timestamp": "2026-01-01T00:00:00Z"}]},
            {"id": "claude-session-1", "workspaceId": "5a0e0b0b-0000-4000-8000-0000000000a1", "kind": "claude"},
        ],
        "activeWorkspaceId": "5a0e0b0b-0000-4000-8000-0000000000a1",
        "activeSessionId": "browser-session-1",
    });
    std::fs::write(folder.join("workspace-state.json"), state.to_string()).unwrap();
    let loaded = Store::new(folder.clone(), None, false).load(&Locale::new("en")).unwrap();
    let browser = loaded.sessions().iter().find(|s| s.kind() == "browser").expect("the browser session loads");
    assert_eq!(browser.workspace_profile_key.as_deref(), Some("5a0e0b0b-0000-4000-8000-0000000000a1"));
    assert_eq!(browser.status(), "idle", "a browser session is always idle");
    assert!(browser.logs().is_empty(), "a browser session keeps no log");
    assert!(loaded.sessions().iter().any(|s| s.id() == "claude-session-1"));
    std::fs::remove_dir_all(&folder).unwrap();
}
