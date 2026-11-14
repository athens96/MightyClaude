//! Completion notifications: the observer, the toast and its `select` push (C# MainWindow.CompletionNotification.cs). Owner: W6-A.
//!
//! The seams, with their final signatures; each answers its resting value until W6-A1 fills it.

use std::sync::Arc;

use mighty_run::RunEvent;

use crate::host::Host;

/// Whether the OS lets the app notify (C# `NotificationPermission`): `Verification` while the app may not register with the OS
/// (without `--allow-state-write`, and in a smoke), else allowed or needing the user's permission.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[allow(dead_code, reason = "a seam W6-A1 (the companion box) calls")]
pub enum NotificationPermission {
    Verification,
    Allowed,
    NeedPermission,
}

/// `notifications::permission`: what the companion box's permission row says. Stub: W6-A1 fills it; until then "verification mode".
#[allow(dead_code, reason = "a seam W6-A1 (the companion box) calls")]
pub fn permission(_host: &Host) -> NotificationPermission {
    NotificationPermission::Verification
}

/// A run event (C# `HandleRunEventForNotification`, the last thing the window does with one). Stub: W6-A1 fills it; until then no
/// notification.
pub fn on_event(_host: &Arc<Host>, _event: &RunEvent) {}
