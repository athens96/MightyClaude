//! Child processes with redirected standard streams (C# `ChildProcess`, ChildProcess.cs): created suspended, put in a job object
//! that kills everything in it when its handle closes, and only then started, so neither the child nor anything it starts can
//! outlive the app; no window; UTF-8 through a hidden console of the app's own that every child inherits.

use std::collections::BTreeMap;
use std::ffi::c_void;
use std::io;
use std::os::windows::io::{FromRawHandle, IntoRawHandle, OwnedHandle};
use std::path::{Path, PathBuf};
use std::pin::Pin;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::task::{Context, Poll};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use mighty_platform::{BoxFuture, Child, ChildControl, PlatformError, ProcessHost, Result, SpawnRequest};
use tokio::io::AsyncWrite;
use tokio::net::windows::named_pipe::NamedPipeServer;
use tokio::sync::watch;
use windows_sys::Win32::Foundation::{GENERIC_READ, GENERIC_WRITE, HANDLE, WAIT_OBJECT_0};
use windows_sys::Win32::Security::SECURITY_ATTRIBUTES;
use windows_sys::Win32::Storage::FileSystem::{
    CreateFileW, FILE_FLAG_FIRST_PIPE_INSTANCE, FILE_FLAG_OVERLAPPED, OPEN_EXISTING, PIPE_ACCESS_INBOUND, PIPE_ACCESS_OUTBOUND,
};
use windows_sys::Win32::Storage::FileSystem::{FILE_READ_ATTRIBUTES, FILE_WRITE_ATTRIBUTES};
use windows_sys::Win32::System::Console::{
    AllocConsole, GetConsoleWindow, GetStdHandle, STD_ERROR_HANDLE, STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, SetConsoleCP, SetConsoleOutputCP,
    SetStdHandle,
};
use windows_sys::Win32::System::JobObjects::{AssignProcessToJobObject, TerminateJobObject};
use windows_sys::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};
use windows_sys::Win32::System::Pipes::{CreateNamedPipeW, PIPE_READMODE_BYTE, PIPE_REJECT_REMOTE_CLIENTS, PIPE_TYPE_BYTE, PIPE_WAIT};
use windows_sys::Win32::System::Threading::{
    CREATE_SUSPENDED, CREATE_UNICODE_ENVIRONMENT, CreateProcessW, DeleteProcThreadAttributeList, EXTENDED_STARTUPINFO_PRESENT,
    GetExitCodeProcess, INFINITE, InitializeProcThreadAttributeList, LPPROC_THREAD_ATTRIBUTE_LIST, PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
    PROCESS_INFORMATION, ResumeThread, STARTF_USESTDHANDLES, STARTUPINFOEXW, TerminateProcess, UpdateProcThreadAttribute,
    WaitForSingleObject,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{SW_HIDE, ShowWindow};

use crate::Variables;
use crate::command::command_line;
use crate::win::{active_processes, check, ended, environment_block, job_processes, kill_on_close_job, last_error, owned, raw, wide};

/// Every launch runs alone (C# `WindowsLaunchGate`, ChildProcess.cs:11, 47): the console set-up and the moment the child's
/// inheritable pipe ends exist are never interleaved with another launch's.
static LAUNCH_GATE: Mutex<()> = Mutex::new(());
static CONSOLE_READY: AtomicBool = AtomicBool::new(false);
static PIPE_SERIAL: AtomicU64 = AtomicU64::new(0);

/// UTF-8, the code page the hidden console is set to before every launch (ChildProcess.cs:137).
const UTF8_CODE_PAGE: u32 = 65001;
/// `TerminateJobObject(job, 1)`: the exit code of a killed child (ChildProcess.cs:52).
pub const KILLED_EXIT_CODE: i32 = 1;
/// How long a close waits for the child, and then for its job to empty (ChildProcess.cs:60, 71).
const CLOSE_WAIT: Duration = Duration::from_secs(3);

pub(crate) fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// Starts children as C# `ChildProcess.Start` does on Windows. See [`mighty_platform::ProcessHost`] for the contract.
#[derive(Debug, Clone, Copy, Default)]
pub struct WindowsProcessHost;

impl WindowsProcessHost {
    pub fn new() -> WindowsProcessHost {
        WindowsProcessHost
    }
}

impl ProcessHost for WindowsProcessHost {
    fn spawn(&self, request: SpawnRequest) -> BoxFuture<'_, Result<Child>> {
        Box::pin(async move { start(&request) })
    }

    /// C# `ProviderCatalog.StartCli` (ProviderCatalog.cs:117-126): a batch file (npm's `pi.cmd`) through cmd.exe with every argument
    /// quoted and the working folder never searched ([`crate::command::launch`]); anything else directly, as `ChildProcess.StartInfo`
    /// builds it (the caller's environment, then `NO_COLOR=1`).
    fn cli_request(&self, program: &Path, args: &[String], cwd: &Path, env: &BTreeMap<String, String>) -> Result<SpawnRequest> {
        let resolved = text(program);
        if crate::command::is_batch(&resolved) {
            return crate::command::launch(&resolved, args, cwd, Some(env), None);
        }
        let mut request = SpawnRequest::new(program, args.iter().cloned(), cwd);
        request.env.clear();
        for (name, value) in env {
            set_ignoring_case(&mut request.env, name, value);
        }
        set_ignoring_case(&mut request.env, "NO_COLOR", "1");
        Ok(request)
    }

    /// C# `RunManager.ExecuteAsync`'s shell kind (RunManager.cs:323, 389): `%ComSpec%` (else `C:\Windows\System32\cmd.exe`) run as
    /// `/d /s /c "<command>"`, the command as typed (the `shellCommand` of `ChildProcess.Start`).
    fn shell_request(&self, command: &str, cwd: &Path, env: &BTreeMap<String, String>) -> SpawnRequest {
        let interpreter = std::env::var_os("ComSpec").map(PathBuf::from).unwrap_or_else(|| PathBuf::from(r"C:\Windows\System32\cmd.exe"));
        let mut request = SpawnRequest::new(interpreter, ["/d", "/s", "/c", command], cwd);
        request.env.clear();
        for (name, value) in env {
            set_ignoring_case(&mut request.env, name, value);
        }
        set_ignoring_case(&mut request.env, "NO_COLOR", "1");
        request.shell_command = Some(command.to_string());
        request
    }
}

/// `dictionary[name] = value` on a dictionary that ignores case: an entry already there in another spelling takes the value.
fn set_ignoring_case(map: &mut BTreeMap<String, String>, name: &str, value: &str) {
    let key = map.keys().find(|key| crate::win::equal_ignore_case(key, name)).cloned().unwrap_or_else(|| name.to_string());
    map.insert(key, value.to_string());
}

/// Gives the app, once, a console of its own that every child inherits: cmd built-ins and native CLIs then write UTF-8 into the
/// pipes (C# `EnsureConsole`, ChildProcess.cs:112-120). Windows 11 24H2 can make a console that never has a window
/// (`AllocConsoleWithOptions`); before that one can only be made with its window and hidden at once. The app's own standard
/// handles are put back. A process that already has a console keeps it.
fn ensure_console() {
    if CONSOLE_READY.swap(true, Ordering::SeqCst) {
        return;
    }
    // SAFETY: plain Win32 calls on this process's console and standard handles.
    unsafe {
        if !GetConsoleWindow().is_null() {
            return;
        }
        let (input, output, error) = (GetStdHandle(STD_INPUT_HANDLE), GetStdHandle(STD_OUTPUT_HANDLE), GetStdHandle(STD_ERROR_HANDLE));
        if !windowless_console() {
            AllocConsole();
            ShowWindow(GetConsoleWindow(), SW_HIDE);
        }
        SetStdHandle(STD_INPUT_HANDLE, input);
        SetStdHandle(STD_OUTPUT_HANDLE, output);
        SetStdHandle(STD_ERROR_HANDLE, error);
    }
}

/// ALLOC_CONSOLE_OPTIONS (ChildProcess.cs:192): the mode (2 = a console session without a window), and the show command for a
/// window when one is made.
#[repr(C)]
struct AllocConsoleOptions {
    mode: i32,
    use_show_window: i32,
    show_window: u16,
}

/// A console with no window at all, or false where Windows has no such call (before build 26100) or it gave none
/// (ChildProcess.cs:123-131). Looked up at run time: an import of it would keep the app from loading on older Windows.
fn windowless_console() -> bool {
    const ALLOC_CONSOLE_MODE_NO_WINDOW: i32 = 2;
    const ALLOC_CONSOLE_RESULT_NO_CONSOLE: i32 = 0;
    type AllocConsoleWithOptions = unsafe extern "system" fn(*mut AllocConsoleOptions, *mut i32) -> i32;
    let kernel32 = wide("kernel32.dll");
    // SAFETY: kernel32 is always loaded; the name is NUL-terminated; the entry point has this signature where it exists.
    unsafe {
        let module = GetModuleHandleW(kernel32.as_ptr());
        if module.is_null() {
            return false;
        }
        let Some(entry) = GetProcAddress(module, c"AllocConsoleWithOptions".as_ptr().cast()) else { return false };
        let alloc: AllocConsoleWithOptions = std::mem::transmute(entry);
        let mut options = AllocConsoleOptions { mode: ALLOC_CONSOLE_MODE_NO_WINDOW, use_show_window: 0, show_window: 0 };
        let mut made = ALLOC_CONSOLE_RESULT_NO_CONSOLE;
        alloc(&mut options, &mut made) >= 0 && made != ALLOC_CONSOLE_RESULT_NO_CONSOLE
    }
}

/// One pipe between the app and a child: the app's end overlapped (for tokio) and not inheritable, the child's end synchronous and
/// inheritable, with the rights `CreatePipe` gives its two ends. C# uses `CreatePipe` (ChildProcess.cs:143-144), whose ends are
/// both synchronous; an anonymous pipe is a named pipe underneath, so the child sees the same handle either way.
struct Pipe {
    app: OwnedHandle,
    child: OwnedHandle,
}

fn pipe(app_reads: bool) -> Result<Pipe> {
    let nanos = SystemTime::now().duration_since(UNIX_EPOCH).map(|t| t.as_nanos()).unwrap_or_default();
    let name = wide(format!(r"\\.\pipe\mighty-child-{}-{}-{nanos}", std::process::id(), PIPE_SERIAL.fetch_add(1, Ordering::Relaxed)));
    let access = if app_reads { PIPE_ACCESS_INBOUND } else { PIPE_ACCESS_OUTBOUND };
    // SAFETY: a NUL-terminated name; one instance; default security (the app's end is not inherited).
    let app = owned(unsafe {
        CreateNamedPipeW(
            name.as_ptr(),
            access | FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAG_OVERLAPPED,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
            1,
            4096,
            4096,
            0,
            std::ptr::null(),
        )
    })?;
    let inherit = SECURITY_ATTRIBUTES {
        nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
        lpSecurityDescriptor: std::ptr::null_mut(),
        bInheritHandle: 1,
    };
    let rights = if app_reads { GENERIC_WRITE | FILE_READ_ATTRIBUTES } else { GENERIC_READ | FILE_WRITE_ATTRIBUTES };
    // SAFETY: the pipe just made, opened once by its name with inheritable security.
    let child = owned(unsafe { CreateFileW(name.as_ptr(), rights, 0, &inherit, OPEN_EXISTING, 0, std::ptr::null_mut()) })?;
    Ok(Pipe { app, child })
}

/// The app's end of a pipe as a tokio stream (it must run inside a tokio runtime with I/O enabled).
fn async_pipe(handle: OwnedHandle) -> Result<NamedPipeServer> {
    // SAFETY: an overlapped named-pipe server handle this function owns alone.
    Ok(unsafe { NamedPipeServer::from_raw_handle(handle.into_raw_handle()) }?)
}

/// `PROC_THREAD_ATTRIBUTE_LIST` storage, deleted on drop.
pub(crate) struct AttributeList {
    buffer: Vec<u64>,
}

impl AttributeList {
    pub(crate) fn new(count: u32) -> Result<AttributeList> {
        let mut bytes = 0usize;
        // SAFETY: the documented size query (it fails with ERROR_INSUFFICIENT_BUFFER and sets `bytes`).
        unsafe { InitializeProcThreadAttributeList(std::ptr::null_mut(), count, 0, &mut bytes) };
        let mut list = AttributeList { buffer: vec![0u64; bytes.div_ceil(8).max(1)] };
        // SAFETY: a buffer of at least `bytes` bytes, 8-byte aligned.
        check(unsafe { InitializeProcThreadAttributeList(list.as_ptr(), count, 0, &mut bytes) }).inspect_err(|_| list.buffer.clear())?;
        Ok(list)
    }

    pub(crate) fn as_ptr(&mut self) -> LPPROC_THREAD_ATTRIBUTE_LIST {
        self.buffer.as_mut_ptr().cast()
    }

    /// # Safety
    /// `value` must stay valid until the list's last use (CreateProcessW reads it then).
    pub(crate) unsafe fn set(&mut self, attribute: u32, value: *const c_void, size: usize) -> Result<()> {
        // SAFETY: an initialized list; the caller keeps `value` alive.
        check(unsafe {
            UpdateProcThreadAttribute(self.as_ptr(), 0, attribute as usize, value, size, std::ptr::null_mut(), std::ptr::null())
        })
    }
}

impl Drop for AttributeList {
    fn drop(&mut self) {
        if !self.buffer.is_empty() {
            // SAFETY: a list InitializeProcThreadAttributeList initialized.
            unsafe { DeleteProcThreadAttributeList(self.as_ptr()) };
        }
    }
}

pub(crate) type ExitWatch = watch::Receiver<Option<std::result::Result<i32, i32>>>;

/// Waits on a thread of its own for the process to end and publishes its exit code (C# `Completion`, ChildProcess.cs:29-33):
/// `Ok(code)`, or `Err(win32 error)` when Windows could not say. The thread owns the process handle and closes it after.
pub(crate) fn watch_exit(process: OwnedHandle, name: &str) -> io::Result<(ExitWatch, std::thread::JoinHandle<()>)> {
    let (sender, receiver) = watch::channel(None);
    let thread = std::thread::Builder::new().name(name.to_string()).stack_size(64 * 1024).spawn(move || {
        // SAFETY: a process handle this thread owns.
        let result = unsafe {
            let mut code = 0u32;
            if WaitForSingleObject(raw(&process), INFINITE) == WAIT_OBJECT_0 && GetExitCodeProcess(raw(&process), &mut code) != 0 {
                Ok(code as i32)
            } else {
                Err(io::Error::last_os_error().raw_os_error().unwrap_or(-1))
            }
        };
        sender.send_replace(Some(result));
        drop(process);
    })?;
    Ok((receiver, thread))
}

/// The exit code once `exit` has one.
pub(crate) async fn exited(exit: &ExitWatch) -> Result<i32> {
    let mut exit = exit.clone();
    loop {
        let current = *exit.borrow_and_update();
        if let Some(result) = current {
            return result.map_err(|code| PlatformError::Io(io::Error::from_raw_os_error(code)));
        }
        if exit.changed().await.is_err() {
            return Err(PlatformError::Io(io::Error::other("the process's wait ended without its exit code")));
        }
    }
}

fn validate(request: &SpawnRequest) -> Result<()> {
    if !request.program.is_absolute() || !request.cwd.is_absolute() {
        return Err(PlatformError::Invalid("the program and the working folder need absolute paths".into()));
    }
    let nul = |text: &str| text.contains('\0');
    if request.args.iter().any(|argument| nul(argument)) || request.shell_command.as_deref().is_some_and(nul) {
        return Err(PlatformError::Invalid("an argument holds a NUL".into()));
    }
    // A name may begin with `=` (the hidden `=C:` drive folders std's `env::vars` lists); C# lays any name over the block.
    if request.env.iter().any(|(name, value)| name.is_empty() || name.chars().skip(1).any(|c| c == '=') || nul(name) || nul(value)) {
        return Err(PlatformError::Invalid("invalid child environment".into()));
    }
    if request.env_remove.iter().any(|name| name.is_empty() || name.chars().skip(1).any(|c| c == '=') || nul(name)) {
        return Err(PlatformError::Invalid("invalid child environment".into()));
    }
    Ok(())
}

fn text(path: &Path) -> String {
    path.to_string_lossy().into_owned()
}

/// C# `ChildProcess.StartWindows` (ChildProcess.cs:133-167).
fn start(request: &SpawnRequest) -> Result<Child> {
    validate(request)?;
    let _gate = lock(&LAUNCH_GATE);
    // A hidden inherited console gives cmd built-ins and native CLIs UTF-8 pipes.
    ensure_console();
    // SAFETY: plain calls on the app's console.
    unsafe {
        SetConsoleCP(UTF8_CODE_PAGE);
        SetConsoleOutputCP(UTF8_CODE_PAGE);
    }
    let (stdin, stdout, stderr) = (pipe(false)?, pipe(true)?, pipe(true)?);
    // The app's ends join the runtime's I/O before the job or the child exist: a launch that fails (or panics, outside a runtime
    // with I/O) here leaves no child to end and no job handle to close. Past this point every failure closes the job as it returns
    // (C#'s `finally`, ChildProcess.cs:165).
    let (input, output, error) = match (async_pipe(stdin.app), async_pipe(stdout.app), async_pipe(stderr.app)) {
        (Ok(input), Ok(output), Ok(error)) => (input, output, error),
        (input, output, error) => {
            let failure = [input.err(), output.err(), error.err()].into_iter().flatten().next();
            return Err(failure.unwrap_or_else(|| PlatformError::Io(io::Error::other("pipe"))));
        }
    };
    let job = kill_on_close_job()?;

    let program = text(&request.program);
    let mut command = wide(command_line(&program, &request.args, request.shell_command.as_deref()));
    // `ProcessStartInfo.Environment`: this process's environment without the names the request takes out, then the request's laid
    // over it, names ignoring case.
    let mut variables = Variables::process();
    for name in &request.env_remove {
        variables.remove(name);
    }
    for (name, value) in &request.env {
        variables.set(name.clone(), value.clone());
    }
    let environment = environment_block(variables.iter());
    let (application, directory) = (wide(&request.program), wide(&request.cwd));

    // Only the three pipe ends are inherited, whatever else of the app is inheritable at this moment: C# passes
    // bInheritHandles = TRUE with its launch gate alone (ChildProcess.cs:152), which cannot stop another thread's inheritable
    // handle (a std::process::Command spawn) leaking in. The list keeps the port's contract; the creation flags are C#'s plus
    // EXTENDED_STARTUPINFO_PRESENT, which only says the list is there.
    let inherited: [HANDLE; 3] = [raw(&stdin.child), raw(&stdout.child), raw(&stderr.child)];
    let mut attributes = AttributeList::new(1)?;
    // SAFETY: `inherited` outlives CreateProcessW below.
    unsafe { attributes.set(PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherited.as_ptr().cast(), size_of_val(&inherited))? };
    // SAFETY: a zeroed plain-data struct, filled below.
    let mut startup: STARTUPINFOEXW = unsafe { std::mem::zeroed() };
    startup.StartupInfo.cb = size_of::<STARTUPINFOEXW>() as u32;
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdInput = inherited[0];
    startup.StartupInfo.hStdOutput = inherited[1];
    startup.StartupInfo.hStdError = inherited[2];
    startup.lpAttributeList = attributes.as_ptr();
    // SAFETY: a zeroed plain-data struct CreateProcessW fills.
    let mut child: PROCESS_INFORMATION = unsafe { std::mem::zeroed() };
    // SAFETY: NUL-terminated strings, a mutable command line, a double-NUL UTF-16 environment block, a valid startup struct.
    check(unsafe {
        CreateProcessW(
            application.as_ptr(),
            command.as_mut_ptr(),
            std::ptr::null(),
            std::ptr::null(),
            1,
            CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT,
            environment.as_ptr().cast(),
            directory.as_ptr(),
            &startup.StartupInfo,
            &mut child,
        )
    })?;
    // SAFETY: the handles CreateProcessW just returned, owned here.
    let (process, thread) = unsafe { (OwnedHandle::from_raw_handle(child.hProcess), OwnedHandle::from_raw_handle(child.hThread)) };
    // Into the job before its first instruction, so everything it starts is in the job too (ChildProcess.cs:153).
    // SAFETY: valid job, process and thread handles.
    let started = unsafe { AssignProcessToJobObject(raw(&job), raw(&process)) != 0 && ResumeThread(raw(&thread)) != u32::MAX };
    if !started {
        let error = last_error();
        // SAFETY: the suspended child this launch made.
        unsafe { TerminateProcess(raw(&process), 1) };
        return Err(error);
    }
    drop(thread);
    drop((stdin.child, stdout.child, stderr.child));
    drop(attributes);
    drop(_gate);

    let exit = match watch_exit(process, "mighty-child-wait") {
        Ok((exit, _detached)) => exit,
        Err(error) => {
            // SAFETY: the job this launch made; its child cannot be waited for, so it ends now.
            unsafe { TerminateJobObject(raw(&job), 1) };
            return Err(error.into());
        }
    };
    let control = Arc::new(WindowsChild {
        job: Mutex::new(Some(job)),
        exit,
        members: Mutex::new(Vec::new()),
        killed: AtomicBool::new(false),
        closed: tokio::sync::Mutex::new(false),
    });
    Ok(Child::new(child.dwProcessId, Box::new(ClosingWriter(Some(input))), Box::new(output), Box::new(error), control))
}

/// The child's stdin: `shutdown` closes it (C# `Input.Close()`), so the child reads its end; a named pipe's own shutdown does
/// nothing. A write in flight still reaches the child first.
struct ClosingWriter(Option<NamedPipeServer>);

impl AsyncWrite for ClosingWriter {
    fn poll_write(mut self: Pin<&mut Self>, cx: &mut Context<'_>, bytes: &[u8]) -> Poll<io::Result<usize>> {
        match self.0.as_mut() {
            Some(pipe) => Pin::new(pipe).poll_write(cx, bytes),
            None => Poll::Ready(Err(io::ErrorKind::BrokenPipe.into())),
        }
    }
    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        match self.0.as_mut() {
            Some(pipe) => Pin::new(pipe).poll_flush(cx),
            None => Poll::Ready(Ok(())),
        }
    }
    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        if let Some(pipe) = self.0.as_mut() {
            std::task::ready!(Pin::new(pipe).poll_flush(cx))?;
        }
        self.0 = None;
        Poll::Ready(Ok(()))
    }
}

/// One child's job and exit (C# `ChildProcess` without its streams, which [`Child`] holds).
pub(crate) struct WindowsChild {
    /// None once a close has begun and ended, finished or dropped half-way.
    job: Mutex<Option<OwnedHandle>>,
    exit: ExitWatch,
    /// The job's processes at the first kill, until a close has seen each of them end.
    members: Mutex<Vec<OwnedHandle>>,
    killed: AtomicBool,
    /// Held by the close that runs, so closes run one at a time; true once one has finished (C# `disposed`).
    closed: tokio::sync::Mutex<bool>,
}

/// Closes the job's handle when the close holding it ends, however it ends: a close dropped half-way (a cancelled task) still ends
/// everything left in the job, as C#'s `finally` does (ChildProcess.cs:81).
struct CloseJob<'a>(&'a Mutex<Option<OwnedHandle>>);

impl Drop for CloseJob<'_> {
    fn drop(&mut self) {
        drop(lock(self.0).take());
    }
}

impl ChildControl for WindowsChild {
    /// `TerminateJobObject(job, 1)` (ChildProcess.cs:50-53): the child and everything it started, at once. The first kill also
    /// keeps a handle to each process the job held, for the close to wait on.
    fn kill(&self) {
        if let Some(job) = lock(&self.job).as_ref() {
            if !self.killed.swap(true, Ordering::SeqCst) {
                *lock(&self.members) = job_processes(raw(job));
            }
            // SAFETY: a job handle this control owns.
            unsafe { TerminateJobObject(raw(job), KILLED_EXIT_CODE as u32) };
        }
    }

    fn wait(&self) -> BoxFuture<'_, Result<i32>> {
        Box::pin(exited(&self.exit))
    }

    /// C# `DisposeAsync` (ChildProcess.cs:54-85): kill; wait up to 3 s for the exit; then, since `TerminateJobObject` is
    /// asynchronous and the parent can already have exited while a descendant still owns the workspace's files, wait up to 3 s
    /// more until the job holds no process; finally close the job (which ends anything left). A close waits for one already
    /// running; a second close after one that finished does nothing. A close dropped half-way has still closed the job, and the
    /// next close waits for the exit and the job's processes again and finishes.
    ///
    /// Beyond C#: the job's count of active processes drops before a terminated process has finished ending (seen here at 0
    /// within 0.1 ms of the kill while a `start /b` descendant still ran for some milliseconds; C#'s check passes only because
    /// `Process.GetProcessById` is slower than that). So the processes the job holds at the kill are also waited for, each until
    /// it has ended, within the same 3 s — what C#'s "job disposal returned while its descendant was alive" check asks.
    fn close(&self) -> BoxFuture<'_, Result<()>> {
        Box::pin(async move {
            let mut closed = self.closed.lock().await;
            if *closed {
                return Ok(());
            }
            let job = CloseJob(&self.job);
            self.kill();
            let outcome = async {
                match tokio::time::timeout(CLOSE_WAIT, exited(&self.exit)).await {
                    Ok(result) => result?,
                    Err(_) => return Err(PlatformError::TimedOut("the child to exit")),
                };
                let started = Instant::now();
                loop {
                    let active = lock(&self.job).as_ref().map(|job| active_processes(raw(job))).transpose()?.unwrap_or(0);
                    if active == 0 && lock(&self.members).iter().all(ended) {
                        return Ok(());
                    }
                    if started.elapsed() >= CLOSE_WAIT {
                        return Err(PlatformError::TimedOut("the child's descendants to close"));
                    }
                    tokio::time::sleep(Duration::from_millis(10)).await;
                }
            }
            .await;
            drop(job);
            lock(&self.members).clear();
            *closed = true;
            outcome
        })
    }
}

/// Dropping a child that was never closed does not end it (the port's contract): its job handle stays open until the app exits,
/// as C#'s does when nothing disposes the child, and then Windows ends the job's processes.
impl Drop for WindowsChild {
    fn drop(&mut self) {
        if let Some(job) = lock(&self.job).take() {
            let _ = job.into_raw_handle();
        }
    }
}

/// Ends `control`'s child and releases it when a task that owned it is dropped half-way (C#'s `await using` on a cancelled
/// token): the kill is immediate; the close, which waits, runs on the runtime.
pub(crate) struct CloseOnDrop(pub(crate) Option<Arc<dyn ChildControl>>);

impl CloseOnDrop {
    pub(crate) fn disarm(mut self) -> Arc<dyn ChildControl> {
        self.0.take().expect("armed")
    }
}

impl Drop for CloseOnDrop {
    fn drop(&mut self) {
        if let Some(control) = self.0.take() {
            control.kill();
            if let Ok(runtime) = tokio::runtime::Handle::try_current() {
                runtime.spawn(async move {
                    let _ = control.close().await;
                });
            }
        }
    }
}
