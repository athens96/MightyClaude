//! Whether a shell pane is running a command (C# `ShellActivity` and `ShellProcessProbe`, ShellActivity.cs; Swift
//! ShellActivity.swift, ShellProcessProbe.swift). The Mac reads the terminal's foreground process group; Windows has none, so a
//! command is running while a process of the shell's job object other than the shell is attached to the shell's own console: a
//! program started in a window of its own, a GUI program and a detached one are not occupying the terminal. It is decided over
//! a snapshot of the job, so it holds without a terminal.

use mighty_platform::PtyControl;
use windows_sys::Wdk::System::Threading::NtQueryInformationProcess;
use windows_sys::Win32::System::Threading::{OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION};

use crate::win::owned;

/// One process of the shell's job: its id and the console it is attached to as Windows records it (0 when none could be read).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Entry {
    pub id: u32,
    pub console: i64,
}

/// True when a process of the job other than the shell shares the shell's console; never when the shell's console is unknown
/// (ShellActivity.cs:18-22).
pub fn is_busy(shell: u32, job: &[Entry]) -> bool {
    let console = job.iter().find(|entry| entry.id == shell).map(|entry| entry.console).unwrap_or(0);
    console != 0 && job.iter().any(|entry| entry.id != shell && entry.console == console)
}

/// The status a shell pane moves to after a reading, or None to leave it (ShellActivity.cs:29-34): a pane that has completed or
/// failed keeps that, a reading that could not be taken (`busy` None) changes nothing, and only a different status is reported.
pub fn next_status(current: &str, busy: Option<bool>) -> Option<&'static str> {
    let running = busy?;
    if matches!(current, "completed" | "error") {
        return None;
    }
    let next = if running { "running" } else { "idle" };
    (next != current).then_some(next)
}

/// `ProcessConsoleHostProcess`, the information class the kernel answers with a process's console (ShellActivity.cs:68).
const PROCESS_CONSOLE_HOST_PROCESS: i32 = 49;

/// The console a process is attached to, as the kernel records it (`NtQueryInformationProcess(ProcessConsoleHostProcess)`, which
/// System Informer reads too): the console host's id for a process on a console, something else for one without. None when the
/// process is gone or may not be asked (ShellActivity.cs:55-62).
pub fn console(process_id: u32) -> Option<i64> {
    // SAFETY: a query-only open of a process id.
    let process = owned(unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, process_id) }).ok()?;
    let mut value: isize = 0;
    let mut returned = 0u32;
    // SAFETY: a pointer-sized out value for this class.
    let status = unsafe {
        NtQueryInformationProcess(
            crate::win::raw(&process),
            PROCESS_CONSOLE_HOST_PROCESS,
            (&mut value as *mut isize).cast(),
            size_of::<isize>() as u32,
            &mut returned,
        )
    };
    (status == 0 && value != 0).then_some(value as i64)
}

/// C# `ShellProcessProbe.IsBusy` (ShellActivity.cs:48-52): whether the terminal's shell (`shell`, its process id) runs a command
/// now; None when its job or its console could not be read — never "idle" for "not known".
pub fn probe(terminal: &dyn PtyControl, shell: u32) -> Option<bool> {
    let ids = terminal.process_ids()?;
    let shell_console = console(shell)?;
    let mut job = vec![Entry { id: shell, console: shell_console }];
    job.extend(ids.into_iter().filter(|id| *id != shell).map(|id| Entry { id, console: console(id).unwrap_or(0) }));
    Some(is_busy(shell, &job))
}
