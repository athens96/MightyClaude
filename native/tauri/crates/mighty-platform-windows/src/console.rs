//! The terminal window on Windows (C# `CliAccountTerminal.LaunchPlan`, CliAccounts.cs:579-, and the `Process.Start` that follows it
//! in `StartSignInAsync` and `StartSetupInstall`). Owner: W6-A.
//!
//! Windows Terminal when `wt` is found along the command's PATH (a new tab, its title and folder given), else the system folder's
//! console host. The process is started with `CreateProcessW` in a console of its own (`CREATE_NEW_CONSOLE`: the app keeps a hidden
//! console its children share, which this window must not join), inheriting no handle and put in no job, so it outlives the app as
//! C#'s `Process.Start` does; `clippy.toml` keeps `std::process::Command` out of the app, and this is not one.

use std::ffi::OsStr;
use std::os::windows::io::{FromRawHandle, OwnedHandle};
use std::path::PathBuf;

use mighty_platform::{BoxFuture, PlatformError, Result, TerminalLaunch, TerminalLauncher, TerminalWindow};
use windows_sys::Win32::System::Threading::{
    CREATE_NEW_CONSOLE, CREATE_UNICODE_ENVIRONMENT, CreateProcessW, PROCESS_INFORMATION, STARTUPINFOW,
};

use crate::command::{NO_CURRENT_DIRECTORY_SEARCH, command_interpreter, command_line, file_exists, find, is_batch, variables};
use crate::process::{ExitWatch, exited, watch_exit};
use crate::win::{check, environment_block, system_directory, wide};

/// `CliAccountTerminal.ConsoleHost`: conhost.exe from the system folder (by name only where Windows gives none, in the checks).
pub fn console_host() -> String {
    match system_directory().filter(|system| !system.as_os_str().is_empty()) {
        Some(system) => system.join("conhost.exe").to_string_lossy().into_owned(),
        None => "conhost.exe".into(),
    }
}

/// `CliAccountTerminal.LaunchPlan`: the program to start and its arguments, so that `argv` runs in a window of its own. Windows
/// Terminal takes a tab (`new-tab [--title t] [-d folder] -- …`) unless a command, the resolved path, `wt`'s own path, the title or
/// the folder hold `;` (its command separator) or `%` (which it expands): then the console host. The resolved path replaces the
/// name; a `.cmd`/`.bat` shim runs through `cmd /c`. `cmd` is cmd.exe's path, `conhost` the console host's.
pub fn launch_plan(
    argv: &[String],
    windows_terminal: Option<&str>,
    directory: Option<&str>,
    resolved: Option<&str>,
    title: Option<&str>,
    cmd: &str,
    conhost: &str,
) -> Result<(String, Vec<String>)> {
    if argv.first().is_none_or(String::is_empty) {
        return Err(PlatformError::Invalid("a login command is required".into()));
    }
    let splits = |text: Option<&str>| text.is_some_and(|t| t.contains(';') || t.contains('%'));
    let shim = resolved.is_some_and(is_batch);
    let rest = &argv[1..];
    let through_shim =
        |binary: &str| std::iter::once(cmd.to_string()).chain(["/c".to_string(), binary.to_string()]).chain(rest.iter().cloned());
    if let Some(terminal) = windows_terminal
        && !splits(resolved)
        && !splits(Some(terminal))
        && !argv.iter().any(|a| splits(Some(a)))
    {
        let mut tab = vec!["new-tab".to_string()];
        if let Some(title) = title.filter(|t| !t.is_empty() && !splits(Some(t))) {
            tab.extend(["--title".to_string(), title.to_string()]);
        }
        if let Some(directory) = directory.filter(|d| !splits(Some(d))) {
            tab.extend(["-d".to_string(), directory.to_string()]);
        }
        tab.push("--".into());
        match resolved.filter(|_| shim) {
            Some(binary) => tab.extend(through_shim(binary)),
            None => tab.extend(argv.iter().cloned()),
        }
        return Ok((terminal.to_string(), tab));
    }
    let Some(binary) = resolved else { return Ok((conhost.to_string(), argv.to_vec())) };
    let arguments: Vec<String> =
        if shim { through_shim(binary).collect() } else { std::iter::once(binary.to_string()).chain(rest.iter().cloned()).collect() };
    Ok((conhost.to_string(), arguments))
}

/// The terminal window launcher of Windows.
#[derive(Debug, Default)]
pub struct WindowsTerminalLauncher;

/// A window started: whether the console host holds the command, and its process's exit as a thread of its own waits for it.
struct Window {
    holds_command: bool,
    exit: ExitWatch,
}

impl TerminalWindow for Window {
    fn holds_command(&self) -> bool {
        self.holds_command
    }

    fn wait(&self) -> BoxFuture<'_, Result<i32>> {
        Box::pin(exited(&self.exit))
    }
}

impl TerminalLauncher for WindowsTerminalLauncher {
    fn open(&self, launch: TerminalLaunch) -> Result<Box<dyn TerminalWindow>> {
        let environment = variables(launch.environment.iter().map(|(k, v)| (k.as_str(), v.as_str())));
        // Windows Terminal by the full path found along the command's PATH, the console host by the system folder's: never a bare name.
        let terminal =
            launch.prefer_terminal_app.then(|| find("wt", environment.get("PATH"), environment.get("PATHEXT"), &file_exists)).flatten();
        let directory = launch.directory.as_ref().filter(|d| d.is_dir()).map(|d| d.to_string_lossy().into_owned());
        let cmd = command_interpreter(Some(&environment)).to_string_lossy().into_owned();
        let conhost = console_host();
        let (program, arguments) = launch_plan(
            &launch.argv,
            terminal.as_deref(),
            directory.as_deref(),
            launch.resolved.as_deref(),
            launch.title.as_deref(),
            &cmd,
            &conhost,
        )?;
        let holds_command = program == conhost;
        // A command that is an npm .cmd shim must not find a planted node.exe in the folder it starts in.
        let mut variables: Vec<(String, String)> = launch.environment.into_iter().collect();
        variables.retain(|(name, _)| !name.eq_ignore_ascii_case(NO_CURRENT_DIRECTORY_SEARCH));
        variables.push((NO_CURRENT_DIRECTORY_SEARCH.to_string(), "1".to_string()));
        let process = start(&program, &arguments, directory.map(PathBuf::from), &variables)?;
        let (exit, _thread) = watch_exit(process, "mighty-terminal-wait")?;
        Ok(Box::new(Window { holds_command, exit }))
    }
}

/// `Process.Start(new ProcessStartInfo(program) { UseShellExecute = false, ArgumentList, WorkingDirectory, Environment })` in a new
/// console: no inherited handle, no job, not suspended. The process handle it answers is the caller's.
fn start(program: &str, arguments: &[String], directory: Option<PathBuf>, variables: &[(String, String)]) -> Result<OwnedHandle> {
    let application = wide(OsStr::new(program));
    let mut line = wide(OsStr::new(&command_line(program, arguments, None)));
    let block = environment_block(variables.iter().map(|(k, v)| (k.as_str(), v.as_str())));
    let directory = directory.map(|d| wide(d.as_os_str()));
    // SAFETY: a zeroed plain-data struct with its size set.
    let mut startup: STARTUPINFOW = unsafe { std::mem::zeroed() };
    startup.cb = size_of::<STARTUPINFOW>() as u32;
    // SAFETY: a zeroed plain-data struct CreateProcessW fills.
    let mut started: PROCESS_INFORMATION = unsafe { std::mem::zeroed() };
    // SAFETY: NUL-terminated strings that live across the call, a mutable command line, a double-NUL UTF-16 environment block, a
    // valid startup struct; no handle is inherited.
    check(unsafe {
        CreateProcessW(
            application.as_ptr(),
            line.as_mut_ptr(),
            std::ptr::null(),
            std::ptr::null(),
            0,
            CREATE_NEW_CONSOLE | CREATE_UNICODE_ENVIRONMENT,
            block.as_ptr().cast(),
            directory.as_ref().map_or(std::ptr::null(), |d| d.as_ptr()),
            &startup,
            &mut started,
        )
    })?;
    // SAFETY: the handles CreateProcessW just returned, owned here; the thread's is not needed.
    let (process, thread) = unsafe { (OwnedHandle::from_raw_handle(started.hProcess), OwnedHandle::from_raw_handle(started.hThread)) };
    drop(thread);
    Ok(process)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn argv(items: &[&str]) -> Vec<String> {
        items.iter().map(|s| s.to_string()).collect()
    }

    const WT: &str = r"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe";
    const CMD: &str = r"C:\Windows\System32\cmd.exe";
    const CONHOST: &str = r"C:\Windows\System32\conhost.exe";

    /// C# `LaunchPlan`: a Windows Terminal tab with its title and folder, the command after `--`.
    #[test]
    fn windows_terminal_takes_a_tab_with_its_title_and_folder() {
        let (program, arguments) =
            launch_plan(&argv(&["claude", "/login"]), Some(WT), Some(r"C:\work\repo"), None, Some("Claude sign-in"), CMD, CONHOST).unwrap();
        assert_eq!(program, WT);
        assert_eq!(arguments, argv(&["new-tab", "--title", "Claude sign-in", "-d", r"C:\work\repo", "--", "claude", "/login"]));
    }

    /// A shim runs through `cmd /c` in either window; the resolved path replaces the name in the console host.
    #[test]
    fn a_shim_runs_through_cmd_and_the_resolved_path_replaces_the_name() {
        let shim = r"C:\Users\me\AppData\Roaming\npm\pi.cmd";
        let (_, tab) = launch_plan(&argv(&["pi", "login"]), Some(WT), None, Some(shim), None, CMD, CONHOST).unwrap();
        assert_eq!(tab, argv(&["new-tab", "--", CMD, "/c", shim, "login"]));
        let (program, arguments) = launch_plan(&argv(&["pi", "login"]), None, None, Some(shim), None, CMD, CONHOST).unwrap();
        assert_eq!((program.as_str(), arguments), (CONHOST, argv(&[CMD, "/c", shim, "login"])));
        let exe = r"C:\tools\agy.exe";
        let (program, arguments) = launch_plan(&argv(&["agy", "auth"]), None, None, Some(exe), None, CMD, CONHOST).unwrap();
        assert_eq!((program.as_str(), arguments), (CONHOST, argv(&[exe, "auth"])));
        let (program, arguments) = launch_plan(&argv(&["gemini"]), None, None, None, None, CMD, CONHOST).unwrap();
        assert_eq!((program.as_str(), arguments), (CONHOST, argv(&["gemini"])), "nothing resolved: the name as it is");
    }

    /// `;` and `%` would be read by Windows Terminal: such a command runs in the console host; a title or folder holding one is left
    /// out of the tab instead.
    #[test]
    fn what_windows_terminal_would_split_goes_to_the_console_host() {
        let (program, _) = launch_plan(&argv(&["claude", "a;b"]), Some(WT), None, None, None, CMD, CONHOST).unwrap();
        assert_eq!(program, CONHOST);
        let (program, _) = launch_plan(&argv(&["claude"]), Some(WT), None, Some(r"C:\100%\claude.exe"), None, CMD, CONHOST).unwrap();
        assert_eq!(program, CONHOST);
        let (program, tab) = launch_plan(&argv(&["claude"]), Some(WT), Some(r"C:\a;b"), None, Some("x%y"), CMD, CONHOST).unwrap();
        assert_eq!((program.as_str(), tab), (WT, argv(&["new-tab", "--", "claude"])));
        assert!(launch_plan(&[], Some(WT), None, None, None, CMD, CONHOST).is_err());
        assert!(launch_plan(&argv(&[""]), None, None, None, None, CMD, CONHOST).is_err());
    }

    #[test]
    fn the_console_host_is_the_system_folders() {
        assert!(console_host().to_ascii_lowercase().ends_with(r"\system32\conhost.exe"));
    }

    /// An empty command opens nothing.
    #[test]
    fn an_empty_command_opens_no_window() {
        assert!(WindowsTerminalLauncher.open(TerminalLaunch::default()).is_err());
    }
}
