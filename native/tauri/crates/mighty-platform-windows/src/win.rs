//! The Win32 pieces the process, terminal and environment modules share (C# `ChildProcess.Native`): wide strings, handles, the
//! environment block, the kill-on-close job, and .NET's `StringComparer.OrdinalIgnoreCase`.

use std::cmp::Ordering;
use std::ffi::{OsStr, c_void};
use std::os::windows::ffi::{OsStrExt, OsStringExt};
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::path::PathBuf;

use mighty_platform::{PlatformError, Result};
use windows_sys::Win32::Foundation::{HANDLE, INVALID_HANDLE_VALUE};
use windows_sys::Win32::System::JobObjects::{
    CreateJobObjectW, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, JOBOBJECT_BASIC_ACCOUNTING_INFORMATION, JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
    JobObjectBasicAccountingInformation, JobObjectExtendedLimitInformation, QueryInformationJobObject, SetInformationJobObject,
};
use windows_sys::Win32::System::SystemInformation::GetSystemDirectoryW;
use windows_sys::core::BOOL;

/// `text` as UTF-16 with a closing NUL.
pub(crate) fn wide(text: impl AsRef<OsStr>) -> Vec<u16> {
    text.as_ref().encode_wide().chain(std::iter::once(0)).collect()
}

/// The calling thread's last Win32 error (C# `new Win32Exception()`).
pub(crate) fn last_error() -> PlatformError {
    PlatformError::Io(std::io::Error::last_os_error())
}

pub(crate) fn check(ok: BOOL) -> Result<()> {
    if ok == 0 { Err(last_error()) } else { Ok(()) }
}

/// Takes ownership of a handle a Win32 call returned; an error when it is null or invalid.
pub(crate) fn owned(handle: HANDLE) -> Result<OwnedHandle> {
    if handle.is_null() || handle == INVALID_HANDLE_VALUE {
        return Err(last_error());
    }
    // SAFETY: a valid handle the caller just received and owns alone.
    Ok(unsafe { OwnedHandle::from_raw_handle(handle) })
}

pub(crate) fn raw(handle: &OwnedHandle) -> HANDLE {
    handle.as_raw_handle() as HANDLE
}

/// One character as `StringComparer.OrdinalIgnoreCase` compares it: its simple uppercase mapping, the character itself when
/// the mapping is not one character (`ß`).
fn fold_char(c: char) -> char {
    if c.is_ascii() {
        return c.to_ascii_uppercase();
    }
    let mut upper = c.to_uppercase();
    match (upper.next(), upper.next()) {
        (Some(single), None) => single,
        _ => c,
    }
}

/// `text` folded for case-blind comparison (the key of [`crate::Variables`]).
pub(crate) fn fold(text: &str) -> String {
    text.chars().map(fold_char).collect()
}

/// `StringComparer.OrdinalIgnoreCase.Compare`: the folded UTF-16 code units in order (C# sorts environment blocks so).
pub(crate) fn compare_ignore_case(a: &str, b: &str) -> Ordering {
    fold(a).encode_utf16().cmp(fold(b).encode_utf16())
}

pub(crate) fn equal_ignore_case(a: &str, b: &str) -> bool {
    a.chars().map(fold_char).eq(b.chars().map(fold_char))
}

/// `ends_with` ignoring case (C# `EndsWith(.., StringComparison.OrdinalIgnoreCase)`).
pub(crate) fn ends_with_ignore_case(text: &str, suffix: &str) -> bool {
    let (text, suffix) = (fold(text), fold(suffix));
    text.ends_with(&suffix)
}

/// `starts_with` ignoring case.
pub(crate) fn starts_with_ignore_case(text: &str, prefix: &str) -> bool {
    let (text, prefix) = (fold(text), fold(prefix));
    text.starts_with(&prefix)
}

/// The system folder Windows reports (C# `Environment.GetFolderPath(Environment.SpecialFolder.System)`), which no environment
/// variable can move.
pub(crate) fn system_directory() -> Option<PathBuf> {
    let mut buffer = vec![0u16; 260];
    loop {
        // SAFETY: `buffer` holds `buffer.len()` writable units.
        let length = unsafe { GetSystemDirectoryW(buffer.as_mut_ptr(), buffer.len() as u32) } as usize;
        if length == 0 {
            return None;
        }
        if length < buffer.len() {
            return Some(PathBuf::from(std::ffi::OsString::from_wide(&buffer[..length])));
        }
        buffer.resize(length + 1, 0);
    }
}

/// `Path.Combine(folder, name)` as .NET writes it on Windows: `name` when it is rooted, else the two joined by `\` unless the
/// folder already ends in a separator or a drive's colon.
pub(crate) fn combine(folder: &str, name: &str) -> String {
    if name.is_empty() {
        return folder.to_string();
    }
    let drive = name.as_bytes().first().is_some_and(u8::is_ascii_alphabetic) && name.as_bytes().get(1) == Some(&b':');
    if folder.is_empty() || name.starts_with(['\\', '/']) || drive {
        return name.to_string();
    }
    if folder.ends_with(['\\', '/', ':']) { format!("{folder}{name}") } else { format!("{folder}\\{name}") }
}

/// The environment block C# hands CreateProcessW (ChildProcess.cs:151, PseudoTerminal.cs:99): `NAME=value` entries ordered by
/// name with `StringComparer.OrdinalIgnoreCase` (a stable sort), each ended by NUL, the block by one more.
pub(crate) fn environment_block<'a>(variables: impl IntoIterator<Item = (&'a str, &'a str)>) -> Vec<u16> {
    let mut pairs: Vec<(&str, &str)> = variables.into_iter().collect();
    pairs.sort_by(|a, b| compare_ignore_case(a.0, b.0));
    let mut block = Vec::new();
    for (name, value) in pairs {
        block.extend(name.encode_utf16());
        block.push(u16::from(b'='));
        block.extend(value.encode_utf16());
        block.push(0);
    }
    // `string.Join('\0', entries) + "\0\0"`: an empty environment is two NULs as well.
    if block.is_empty() {
        block.push(0);
    }
    block.push(0);
    block
}

/// A job object whose processes all end when its last handle closes: `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` (0x2000) through
/// `JobObjectExtendedLimitInformation` (9), as ChildProcess.cs:145-148 and PseudoTerminal.cs:86-89 make it.
pub(crate) fn kill_on_close_job() -> Result<OwnedHandle> {
    // SAFETY: an unnamed job with default security.
    let job = owned(unsafe { CreateJobObjectW(std::ptr::null(), std::ptr::null()) })?;
    // SAFETY: a zeroed plain-data struct.
    let mut limits: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = unsafe { std::mem::zeroed() };
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    // SAFETY: the struct and its size, for the class that reads it.
    check(unsafe {
        SetInformationJobObject(
            raw(&job),
            JobObjectExtendedLimitInformation,
            &limits as *const _ as *const c_void,
            size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
        )
    })?;
    Ok(job)
}

/// How many processes the job still holds (`JobObjectBasicAccountingInformation`, ChildProcess.cs:69).
pub(crate) fn active_processes(job: HANDLE) -> Result<u32> {
    // SAFETY: a zeroed plain-data struct.
    let mut state: JOBOBJECT_BASIC_ACCOUNTING_INFORMATION = unsafe { std::mem::zeroed() };
    // SAFETY: the struct and its size, for the class that fills it.
    check(unsafe {
        QueryInformationJobObject(
            job,
            JobObjectBasicAccountingInformation,
            &mut state as *mut _ as *mut c_void,
            size_of::<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>() as u32,
            std::ptr::null_mut(),
        )
    })?;
    Ok(state.ActiveProcesses)
}

/// The ids of the processes in the job (`JobObjectBasicProcessIdList`, PseudoTerminal.cs:131-147): a list too short for the job
/// fails with ERROR_MORE_DATA and is asked for again, larger (room for 64, 256, 1024, 4096 ids). None when Windows gives no list.
pub(crate) fn job_process_ids(job: HANDLE) -> Option<Vec<u32>> {
    let header = 8 / size_of::<usize>();
    let mut room = 64usize;
    while room <= 4096 {
        // Two DWORD counts, then ULONG_PTR ids.
        let mut list = vec![0usize; header + room];
        // SAFETY: a usize-aligned buffer of the length given.
        let ok = unsafe {
            QueryInformationJobObject(
                job,
                windows_sys::Win32::System::JobObjects::JobObjectBasicProcessIdList,
                list.as_mut_ptr().cast(),
                size_of_val(list.as_slice()) as u32,
                std::ptr::null_mut(),
            )
        };
        if ok != 0 {
            // SAFETY: the second DWORD of the header Windows just filled.
            let count = unsafe { *(list.as_ptr() as *const u32).add(1) } as usize;
            return Some(list[header..header + count.min(room)].iter().map(|id| *id as u32).collect());
        }
        if std::io::Error::last_os_error().raw_os_error() != Some(windows_sys::Win32::Foundation::ERROR_MORE_DATA as i32) {
            return None;
        }
        room *= 4;
    }
    None
}

/// Handles to wait on for each process the job holds now (opened for SYNCHRONIZE, and only while still in this job, so a reused
/// id is never waited for).
pub(crate) fn job_processes(job: HANDLE) -> Vec<OwnedHandle> {
    use windows_sys::Win32::System::JobObjects::IsProcessInJob;
    use windows_sys::Win32::System::Threading::{OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE};
    let ids = job_process_ids(job).unwrap_or_default();
    ids.into_iter()
        .filter_map(|id| {
            // SAFETY: a query-and-wait open of a process id.
            let process = owned(unsafe { OpenProcess(PROCESS_SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, 0, id) }).ok()?;
            let mut member = 0;
            // SAFETY: valid process and job handles.
            let checked = unsafe { IsProcessInJob(raw(&process), job, &mut member) };
            (checked != 0 && member != 0).then_some(process)
        })
        .collect()
}

/// Whether the process (or any waitable handle) is signaled now.
pub(crate) fn ended(process: &OwnedHandle) -> bool {
    use windows_sys::Win32::Foundation::WAIT_OBJECT_0;
    use windows_sys::Win32::System::Threading::WaitForSingleObject;
    // SAFETY: a valid handle, a zero wait.
    unsafe { WaitForSingleObject(raw(process), 0) == WAIT_OBJECT_0 }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_compare_as_ordinal_ignore_case() {
        assert_eq!(compare_ignore_case("Path", "PATH"), Ordering::Equal);
        assert_eq!(compare_ignore_case("a", "B"), Ordering::Less);
        // '_' (0x5F) sorts after the uppercase letters, as .NET compares the uppercased units.
        assert_eq!(compare_ignore_case("A_B", "AB"), Ordering::Greater);
        assert!(equal_ignore_case("pathext", "PATHEXT") && !equal_ignore_case("PATH", "PATHEXT"));
        assert_eq!(fold("straße"), "STRAßE", "a mapping that is not one character keeps the character");
    }

    #[test]
    fn the_environment_block_is_sorted_and_double_terminated() {
        let block = environment_block([("b", "2"), ("A", "1"), ("EMPTY", "")]);
        assert_eq!(String::from_utf16(&block).unwrap(), "A=1\0b=2\0EMPTY=\0\0");
        assert_eq!(environment_block(std::iter::empty()), [0, 0]);
    }

    #[test]
    fn combine_joins_as_dotnet_does() {
        assert_eq!(combine(r"C:\tools", "git.exe"), r"C:\tools\git.exe");
        assert_eq!(combine(r"C:\tools\", "git.exe"), r"C:\tools\git.exe");
        assert_eq!(combine("C:", "git.exe"), "C:git.exe");
        assert_eq!(combine(r"C:\tools", r"D:\x"), r"D:\x");
        assert_eq!(combine("/usr/local/bin", "hermes"), r"/usr/local/bin\hermes");
    }
}
