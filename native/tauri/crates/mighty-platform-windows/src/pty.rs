//! A persistent ConPTY session (C# `PseudoTerminal`, PseudoTerminal.cs). Input, output and teardown run independently;
//! `ClosePseudoConsole` never blocks the caller or the thread draining its output.
//! <https://learn.microsoft.com/windows/console/creating-a-pseudoconsole-session>

use std::ffi::c_void;
use std::io::{Read, Write};
use std::os::windows::io::OwnedHandle;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{SyncSender, TrySendError};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::Duration;

use mighty_platform::{BoxFuture, PlatformError, PtyControl, PtyHost, PtyRequest, PtySession, PtySize, Result};
use tokio::sync::{mpsc, watch};
use windows_sys::Win32::Foundation::HANDLE;
use windows_sys::Win32::System::Console::{COORD, ClosePseudoConsole, CreatePseudoConsole, HPCON, ResizePseudoConsole};
use windows_sys::Win32::System::JobObjects::{AssignProcessToJobObject, TerminateJobObject};
use windows_sys::Win32::System::Pipes::CreatePipe;
use windows_sys::Win32::System::Threading::{
    CREATE_SUSPENDED, CREATE_UNICODE_ENVIRONMENT, CreateProcessW, EXTENDED_STARTUPINFO_PRESENT, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
    PROCESS_INFORMATION, ResumeThread, STARTF_USESTDHANDLES, STARTUPINFOEXW, TerminateProcess,
};

use crate::command::quote_windows;
use crate::process::{AttributeList, ExitWatch, exited, lock, watch_exit};
use crate::win::{
    check, environment_block, equal_ignore_case, job_process_ids, kill_on_close_job, last_error, owned, raw, system_directory, wide,
};

/// The most input written at once, in UTF-16 units of its text: C# `text.Length > 65_536` (PseudoTerminal.cs:127).
const MAXIMUM_WRITE: usize = 65_536;
/// How long the output drain waits before it offers a chunk again while the output's 64 chunks are all taken up.
const OUTPUT_RETRY: Duration = Duration::from_millis(10);

/// ConPTY's wire is UTF-8, but Windows PowerShell 5.1 otherwise uses the machine's legacy console code page for Console.Out and
/// native pipelines. Set only this shell's console; never change the user's profile or system (PseudoTerminal.cs:52-55).
pub const POWERSHELL_UTF8: &str = "[Console]::InputEncoding=[System.Text.UTF8Encoding]::new($false);[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false);$OutputEncoding=[Console]::OutputEncoding;";

/// Windows PowerShell 5.1 when it is there, else `%ComSpec%`, else the system's cmd.exe (C# `PseudoTerminal.DefaultShell`,
/// PseudoTerminal.cs:49-50).
pub fn default_shell() -> PathBuf {
    let system = system_directory().unwrap_or_default();
    let powershell = system.join("WindowsPowerShell").join("v1.0").join("powershell.exe");
    if powershell.is_file() {
        return powershell;
    }
    std::env::var_os("ComSpec").map(PathBuf::from).unwrap_or_else(|| system.join("cmd.exe"))
}

/// C# `PseudoTerminal.ShellArguments` (PseudoTerminal.cs:56-61): Windows PowerShell (`powershell`, by its file name) sets the
/// console to UTF-8 and stays (`-NoExit`) or runs `command`; any other shell is read as cmd.exe and switched to code page 65001.
pub fn shell_arguments(shell: &Path, command: Option<&str>, no_profile: bool) -> Vec<String> {
    let stem = shell.file_stem().map(|stem| stem.to_string_lossy().into_owned()).unwrap_or_default();
    if equal_ignore_case(&stem, "powershell") {
        let mut arguments = vec!["-NoLogo".to_string()];
        if no_profile {
            arguments.push("-NoProfile".into());
        }
        if command.is_none() {
            arguments.push("-NoExit".into());
        }
        arguments.push("-Command".into());
        arguments.push(format!("{POWERSHELL_UTF8}{}", command.unwrap_or("")));
        return arguments;
    }
    match command {
        None => vec!["/d".into(), "/k".into(), "chcp 65001 >nul".into()],
        Some(command) => vec!["/d".into(), "/s".into(), "/c".into(), format!("chcp 65001 >nul & {command}")],
    }
}

/// Opens ConPTY sessions (C# `PseudoTerminal.Start`). A session whose last control is dropped without [`PtyControl::close`] is not
/// torn down, as C#'s (which nothing finalizes) is not: its job stays open until the app exits, though its input closes with the
/// control, which ends a shell that reads its input to the end.
#[derive(Debug, Clone, Copy, Default)]
pub struct WindowsPtyHost;

impl WindowsPtyHost {
    pub fn new() -> WindowsPtyHost {
        WindowsPtyHost
    }
}

impl PtyHost for WindowsPtyHost {
    fn default_shell(&self) -> PathBuf {
        default_shell()
    }

    fn shell_arguments(&self, shell: &Path, command: Option<&str>, no_profile: bool) -> Vec<String> {
        shell_arguments(shell, command, no_profile)
    }

    fn open(&self, request: PtyRequest) -> BoxFuture<'_, Result<PtySession>> {
        Box::pin(async move { start(&request) })
    }
}

fn coord(size: PtySize) -> COORD {
    let size = size.clamped();
    COORD { X: size.columns as i16, Y: size.rows as i16 }
}

fn hresult(code: i32) -> Result<()> {
    if code < 0 { Err(PlatformError::Io(std::io::Error::from_raw_os_error(code))) } else { Ok(()) }
}

/// An anonymous pipe neither of whose ends is inherited (PseudoTerminal.cs:77-78): (read, write).
fn anonymous_pipe() -> Result<(OwnedHandle, OwnedHandle)> {
    let (mut read, mut write): (HANDLE, HANDLE) = (std::ptr::null_mut(), std::ptr::null_mut());
    // SAFETY: two out-pointers; default (non-inheritable) security.
    check(unsafe { CreatePipe(&mut read, &mut write, std::ptr::null(), 0) })?;
    Ok((owned(read)?, owned(write)?))
}

/// What a launch holds until it has a terminal, released in C#'s order on failure (PseudoTerminal.cs:113-122): with no output
/// reader yet, the pipes close before the console, so `ClosePseudoConsole` has nothing to wait on.
struct Pending {
    pipes: Vec<OwnedHandle>,
    console: HPCON,
}

impl Drop for Pending {
    fn drop(&mut self) {
        self.pipes.clear();
        if self.console != 0 {
            // SAFETY: a console CreatePseudoConsole made and nothing else closes.
            unsafe { ClosePseudoConsole(self.console) };
        }
    }
}

/// C# `PseudoTerminal.Start` (PseudoTerminal.cs:63-123).
fn start(request: &PtyRequest) -> Result<PtySession> {
    if !request.cwd.is_absolute() || !request.cwd.is_dir() {
        return Err(PlatformError::Invalid("a local workspace directory is required".into()));
    }
    if !request.program.is_absolute() || !request.program.is_file() {
        return Err(PlatformError::Io(std::io::Error::new(std::io::ErrorKind::NotFound, "the terminal shell was not found")));
    }
    if request.env.iter().any(|(name, value)| name.is_empty() || name.contains('=') || name.contains('\0') || value.contains('\0')) {
        return Err(PlatformError::Invalid("invalid terminal environment".into()));
    }
    let (input_read, input_write) = anonymous_pipe()?;
    let (output_read, output_write) = anonymous_pipe()?;
    let mut pending = Pending { pipes: Vec::new(), console: 0 };
    let mut console: HPCON = 0;
    // SAFETY: the two pipe ends ConPTY reads and writes; `console` receives the new pseudo console.
    hresult(unsafe { CreatePseudoConsole(coord(request.size), raw(&input_read), raw(&output_write), 0, &mut console) })?;
    pending.console = console;
    pending.pipes.extend([input_read, input_write, output_read, output_write]);
    let mut attributes = AttributeList::new(1)?;
    // SAFETY: for this attribute the value is the console handle itself; the console outlives CreateProcessW.
    unsafe { attributes.set(PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, console as *const c_void, size_of::<HPCON>())? };
    let job = kill_on_close_job()?;
    // Explicit null standard handles prevent Windows from duplicating redirected parent streams into this child instead of
    // connecting them to its pseudoconsole (also affects launchers and CI hosts). https://github.com/microsoft/terminal/discussions/15814
    // SAFETY: a zeroed plain-data struct, filled below.
    let mut startup: STARTUPINFOEXW = unsafe { std::mem::zeroed() };
    startup.StartupInfo.cb = size_of::<STARTUPINFOEXW>() as u32;
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.lpAttributeList = attributes.as_ptr();
    let program = request.program.to_string_lossy().into_owned();
    let line =
        std::iter::once(program.as_str()).chain(request.args.iter().map(String::as_str)).map(quote_windows).collect::<Vec<_>>().join(" ");
    let mut command = wide(line);
    let environment = environment_block(request.env.iter().map(|(name, value)| (name.as_str(), value.as_str())));
    let (application, directory) = (wide(&request.program), wide(&request.cwd));
    // SAFETY: zeroed, filled by CreateProcessW.
    let mut child: PROCESS_INFORMATION = unsafe { std::mem::zeroed() };
    // SAFETY: NUL-terminated strings, a mutable command line, a UTF-16 environment block, an extended startup struct. Nothing is
    // inherited (bInheritHandles FALSE); the flags are C#'s 0x00080404.
    check(unsafe {
        CreateProcessW(
            application.as_ptr(),
            command.as_mut_ptr(),
            std::ptr::null(),
            std::ptr::null(),
            0,
            EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED,
            environment.as_ptr().cast(),
            directory.as_ptr(),
            &startup.StartupInfo,
            &mut child,
        )
    })?;
    // SAFETY: the handles CreateProcessW returned, owned here.
    let (process, thread) = unsafe {
        use std::os::windows::io::FromRawHandle;
        (OwnedHandle::from_raw_handle(child.hProcess), OwnedHandle::from_raw_handle(child.hThread))
    };
    // SAFETY: valid job, process and thread handles.
    if unsafe { AssignProcessToJobObject(raw(&job), raw(&process)) == 0 || ResumeThread(raw(&thread)) == u32::MAX } {
        let error = last_error();
        // SAFETY: the suspended child this launch made.
        unsafe { TerminateProcess(raw(&process), 1) };
        return Err(error);
    }
    drop(thread);
    drop(attributes);
    // ConPTY holds its own copies of the console's ends; the app keeps only its input's write end and its output's read end.
    let mut pipes = std::mem::take(&mut pending.pipes).into_iter();
    let (input_read, input_write, output_read, output_write) =
        (pipes.next().expect("pipe"), pipes.next().expect("pipe"), pipes.next().expect("pipe"), pipes.next().expect("pipe"));
    drop((input_read, output_write));

    let (output_sender, output) = mpsc::channel(64);
    let (writes, queue) = std::sync::mpsc::sync_channel::<Vec<u8>>(64);
    let closing = Arc::new(AtomicBool::new(false));
    let draining = closing.clone();
    // The output drain: ends once the console has closed and its final frame is read. While the output's 64 chunks are all taken
    // up it waits (so the console waits on its pipe and the memory held stays bounded), but never once the close has begun: from
    // then a chunk that finds no room is dropped, so the console's pipe always empties and the close never waits on a reader that
    // waits on a consumer (C#'s reader hands each chunk to `receive` and never waits, PseudoTerminal.cs:31). When nobody takes the
    // output any more it keeps reading and drops what it reads.
    let reader = std::thread::Builder::new().name("mighty-pty-output".into()).spawn(move || {
        let mut pipe = std::fs::File::from(output_read);
        let mut buffer = vec![0u8; 8192];
        let mut sender = Some(output_sender);
        while let Ok(read @ 1..) = pipe.read(&mut buffer) {
            let mut chunk = buffer[..read].to_vec();
            while let Some(channel) = &sender {
                match channel.try_send(chunk) {
                    Ok(()) => break,
                    Err(mpsc::error::TrySendError::Closed(_)) => {
                        sender = None;
                        break;
                    }
                    Err(mpsc::error::TrySendError::Full(_)) if draining.load(Ordering::SeqCst) => break,
                    Err(mpsc::error::TrySendError::Full(back)) => {
                        chunk = back;
                        // The close unparks this thread, so it stops waiting at once.
                        std::thread::park_timeout(OUTPUT_RETRY);
                    }
                }
            }
        }
    });
    let writer = std::thread::Builder::new().name("mighty-pty-input".into()).spawn(move || {
        let mut pipe = std::fs::File::from(input_write);
        while let Ok(bytes) = queue.recv() {
            if pipe.write_all(&bytes).and_then(|()| pipe.flush()).is_err() {
                break;
            }
        }
    });
    let exit = watch_exit(process, "mighty-pty-wait");
    let (reader, writer, (exit, waiter)) = match (reader, writer, exit) {
        (Ok(reader), Ok(writer), Ok(exit)) => (reader, writer, exit),
        (reader, writer, exit) => {
            // SAFETY: the job this launch made.
            unsafe { TerminateJobObject(raw(&job), 1) };
            let failure = [reader.err(), writer.err(), exit.err()].into_iter().flatten().next();
            return Err(PlatformError::Io(failure.unwrap_or_else(|| std::io::Error::other("terminal threads"))));
        }
    };
    let pid = child.dwProcessId;
    let console = std::mem::take(&mut pending.console);
    drop(pending);
    let (done, _) = watch::channel(false);
    let terminal = Terminal {
        lifecycle: Mutex::new(Lifecycle { console, job: Some(job), writes: Some(writes), threads: Some([reader, writer, waiter]) }),
        closing,
        exit,
        done: Arc::new(done),
        pid,
    };
    Ok(PtySession { id: pid, output, control: Arc::new(terminal) })
}

struct Lifecycle {
    /// 0 once closed.
    console: HPCON,
    job: Option<OwnedHandle>,
    writes: Option<SyncSender<Vec<u8>>>,
    /// The output drain, the input writer and the exit wait, joined by the close.
    threads: Option<[JoinHandle<()>; 3]>,
}

/// One session's input, size, process list and teardown (C# `PseudoTerminal` past its start).
struct Terminal {
    lifecycle: Mutex<Lifecycle>,
    /// The close has begun (C# `disposal`); set under `lifecycle`'s lock, read by the output drain without it.
    closing: Arc<AtomicBool>,
    exit: ExitWatch,
    done: Arc<watch::Sender<bool>>,
    /// The shell's process id (C# `ProcessId`), which the shell-activity reading starts from.
    pid: u32,
}

impl Terminal {
    fn has_exited(&self) -> bool {
        self.exit.borrow().is_some()
    }
}

/// A session nobody closed keeps its job handle until the app exits (see [`WindowsPtyHost`]).
impl Drop for Terminal {
    fn drop(&mut self) {
        let mut state = lock(&self.lifecycle);
        if !self.closing.load(Ordering::SeqCst)
            && let Some(job) = state.job.take()
        {
            let _ = std::os::windows::io::IntoRawHandle::into_raw_handle(job);
        }
    }
}

impl PtyControl for Terminal {
    /// C# `TryWrite` (PseudoTerminal.cs:125-129): text of at most 65,536 UTF-16 units (C#'s `text.Length`: 30,000 Korean
    /// characters, 90,000 bytes, are taken), not once closing or once the shell has exited, and only while the 64-write queue has
    /// room. UTF-8 never has fewer bytes than its text has UTF-16 units, so only a longer write is counted.
    fn write(&self, bytes: &[u8]) -> bool {
        if bytes.len() > MAXIMUM_WRITE && String::from_utf8_lossy(bytes).encode_utf16().count() > MAXIMUM_WRITE {
            return false;
        }
        let state = lock(&self.lifecycle);
        if self.closing.load(Ordering::SeqCst) || self.has_exited() {
            return false;
        }
        match state.writes.as_ref().map(|writes| writes.try_send(bytes.to_vec())) {
            Some(Ok(())) => true,
            Some(Err(TrySendError::Full(_) | TrySendError::Disconnected(_))) | None => false,
        }
    }

    /// C# `Resize` (PseudoTerminal.cs:149-153): clamped to 2–1000 × 1–1000; nothing once closing.
    fn resize(&self, size: PtySize) -> Result<()> {
        let state = lock(&self.lifecycle);
        if self.closing.load(Ordering::SeqCst) || state.console == 0 {
            return Ok(());
        }
        // SAFETY: an open pseudo console.
        hresult(unsafe { ResizePseudoConsole(state.console, coord(size)) })
    }

    /// C# `JobProcessIds` (PseudoTerminal.cs:131-147): the ids in the shell's job (`JobObjectBasicProcessIdList`); a list too short
    /// for the job fails with ERROR_MORE_DATA and is asked for again, larger (64, 256, 1024, 4096 ids).
    fn process_ids(&self) -> Option<Vec<u32>> {
        let state = lock(&self.lifecycle);
        if self.closing.load(Ordering::SeqCst) {
            return None;
        }
        job_process_ids(raw(state.job.as_ref()?))
    }

    /// C# `ShellProcessProbe.IsBusy(terminal)` (ShellActivity.cs:48-52) over this session's job and its shell.
    fn busy(&self) -> Option<bool> {
        crate::shell_activity::probe(self, self.pid)
    }

    fn wait(&self) -> BoxFuture<'_, Result<i32>> {
        Box::pin(exited(&self.exit))
    }

    /// C# `DisposeAsync` / `CloseAsync` (PseudoTerminal.cs:155-172), on a thread of its own so it never blocks the caller: the input
    /// queue is completed, the job terminated, then `ClosePseudoConsole` — while the output drain still runs, so the console's final
    /// frame passes — then the shell's exit, the drain and the writer are awaited, and the job's handle is closed. Every call waits
    /// for the same close. From its start the drain drops what finds the output full rather than wait for it to be read, so the
    /// close returns whether or not anyone reads the output. Should Windows refuse the close its thread, it runs on the caller's.
    fn close(&self) -> BoxFuture<'_, Result<()>> {
        let mut done = self.done.subscribe();
        {
            let mut state = lock(&self.lifecycle);
            if !self.closing.swap(true, Ordering::SeqCst) {
                let writes = state.writes.take();
                let job = state.job.take();
                let console = std::mem::take(&mut state.console);
                let threads = state.threads.take();
                let finished = self.done.clone();
                let teardown = move || {
                    if let Some([reader, ..]) = &threads {
                        // A drain waiting for room in the output sees the close now.
                        reader.thread().unpark();
                    }
                    drop(writes);
                    if let Some(job) = &job {
                        // SAFETY: the session's job.
                        unsafe { TerminateJobObject(raw(job), 1) };
                    }
                    if console != 0 {
                        // SAFETY: the session's console, closed once.
                        unsafe { ClosePseudoConsole(console) };
                    }
                    // The output drain stays alive until ClosePseudoConsole's final frame has passed.
                    for thread in threads.into_iter().flatten() {
                        let _ = thread.join();
                    }
                    drop(job);
                    finished.send_replace(true);
                };
                // The thread takes the teardown; when it cannot be started the teardown is still here, and runs now.
                let teardown = Arc::new(Mutex::new(Some(teardown)));
                let handed = teardown.clone();
                let started = std::thread::Builder::new().name("mighty-pty-close".into()).spawn(move || {
                    let teardown = lock(&handed).take();
                    if let Some(teardown) = teardown {
                        teardown();
                    }
                });
                if started.is_err() {
                    let teardown = lock(&teardown).take();
                    if let Some(teardown) = teardown {
                        drop(state);
                        teardown();
                    }
                }
            }
        }
        Box::pin(async move {
            let _ = done.wait_for(|finished| *finished).await;
            Ok(())
        })
    }
}
