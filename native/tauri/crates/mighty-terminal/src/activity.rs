//! A shell pane's status from its terminal (C# `ShellActivity.NextStatus`, ShellActivity.cs:29-34; Swift ShellActivity.swift):
//! idle at the prompt, running while a command occupies the terminal, completed once the shell exited, error when it could not
//! start. The reading itself is the platform's ([`mighty_platform::PtyControl::busy`]).

use mighty_core::model::RunSession;

/// The shell pane statuses its terminal sets (`DesktopService.SetShellStatus` takes no other).
pub const SHELL_STATUSES: [&str; 4] = ["idle", "running", "completed", "error"];

/// C# `DesktopService.SetShellStatus`'s change of one pane (DesktopService.cs:165-174): a shell pane takes `status` when it is one
/// of [`SHELL_STATUSES`] and not the one it has. The pane's next record and whether it is saved at once — only an exit or a failure
/// is (a restart reads running as idle, Swift StateStore.swift:201) — or None for no change. It is never a run event.
pub fn shell_status_change(pane: &RunSession, status: &str) -> Option<(RunSession, bool)> {
    if !SHELL_STATUSES.contains(&status) || pane.kind() != "shell" || pane.status() == status {
        return None;
    }
    let mut next = pane.clone();
    next.status = Some(status.to_string());
    Some((next, matches!(status, "completed" | "error")))
}

/// The status a shell pane moves to after a reading, or None to leave it: a pane that has completed or failed keeps that, a reading
/// that could not be taken (`busy` None) changes nothing, and only a different status is reported.
pub fn next_status(current: &str, busy: Option<bool>) -> Option<&'static str> {
    let running = busy?;
    if matches!(current, "completed" | "error") {
        return None;
    }
    let next = if running { "running" } else { "idle" };
    (next != current).then_some(next)
}

/// A reading of the shell's activity as the change of one pane (C# `ApplyShellActivity` through `SetShellStatus`): [`next_status`]
/// from the status `pane` has now, then [`shell_status_change`]. Made inside the state's update, so a pane that completed or failed
/// after the reading was taken keeps that.
pub fn activity_change(pane: &RunSession, busy: bool) -> Option<(RunSession, bool)> {
    next_status(pane.status(), Some(busy)).and_then(|next| shell_status_change(pane, next))
}
