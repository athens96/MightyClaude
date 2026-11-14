//! The status line trust rule (C# `StatusLineTrust`, the Mac's AppStore+StatusLine.swift): a `statusLine` that comes from the
//! workspace runs only after the user allows it for that workspace, the stored value is the configuration's fingerprint per
//! workspace id, a changed command asks again, and a user-level command runs without a prompt. The store is the state's defaulted
//! `trustedStatusLines`, so the state's version stays 1.

use mighty_core::model::{AppSnapshot, StringMap};

use crate::status_line::config::{StatusLineConfig, StatusLineDiscovery};

pub fn is_trusted(config: &StatusLineConfig, trusted: Option<&StringMap>, workspace_id: &str) -> bool {
    !config.from_workspace
        || trusted.and_then(|map| map.get(workspace_id)).and_then(|value| value.as_deref()) == Some(config.fingerprint().as_str())
}

pub fn is_trusted_in(snapshot: &AppSnapshot, config: &StatusLineConfig, workspace_id: &str) -> bool {
    is_trusted(config, snapshot.trusted_status_lines.as_ref(), workspace_id)
}

/// 이 워크스페이스에서 허용: records the fingerprint, so an edited command asks again.
pub fn trust(snapshot: &mut AppSnapshot, config: &StatusLineConfig, workspace_id: &str) {
    snapshot.trusted_status_lines.get_or_insert_with(StringMap::new).insert(workspace_id.to_string(), Some(config.fingerprint()));
}

/// What a pane shows: `(the command it may run now, the workspace command still waiting for an answer)`. An untrusted workspace
/// command is never run, but while it waits the user-level command still runs if there is one.
pub fn resolve(
    discovery: &StatusLineDiscovery,
    snapshot: &AppSnapshot,
    workspace_id: &str,
) -> (Option<StatusLineConfig>, Option<StatusLineConfig>) {
    resolve_trusted(discovery, snapshot.trusted_status_lines.as_ref(), workspace_id)
}

/// [`resolve`] over the trusted fingerprints alone (what the refresher reads of the state).
pub fn resolve_trusted(
    discovery: &StatusLineDiscovery,
    trusted: Option<&StringMap>,
    workspace_id: &str,
) -> (Option<StatusLineConfig>, Option<StatusLineConfig>) {
    let Some(preferred) = discovery.preferred() else { return (None, None) };
    let gated = preferred.from_workspace && !is_trusted(preferred, trusted, workspace_id);
    if gated { (discovery.user.clone(), Some(preferred.clone())) } else { (Some(preferred.clone()), None) }
}
