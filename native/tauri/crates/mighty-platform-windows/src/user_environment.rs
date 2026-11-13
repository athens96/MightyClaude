//! The signed-in user's own environment (USERPROFILE, APPDATA, LOCALAPPDATA, TEMP, PATH, PATHEXT …), as Windows builds it for a
//! new logon (`CreateEnvironmentBlock` with no inheritance) (C# `UserEnvironment`, UserEnvironment.cs). The update helper runs with
//! only `SystemRoot`; without this the app it starts again — and every CLI, shell and tool the app then runs — would inherit that
//! bare environment, and tools that need a home folder (Antigravity's `%userprofile% is not defined`, gh, Python installers,
//! Unreal) fail.

use std::ffi::c_void;

use windows_sys::Win32::Foundation::HANDLE;
use windows_sys::Win32::Security::TOKEN_QUERY;
use windows_sys::Win32::System::Environment::{CreateEnvironmentBlock, DestroyEnvironmentBlock};
use windows_sys::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

use crate::Variables;
use crate::win::owned;

/// The variables whose absence marks a bare environment (UserEnvironment.cs:15).
pub const MARKERS: [&str; 3] = ["USERPROFILE", "APPDATA", "LOCALAPPDATA"];

/// True when any marker is missing or empty in `current` (UserEnvironment.cs:18).
pub fn is_bare(current: impl Fn(&str) -> Option<String>) -> bool {
    MARKERS.iter().any(|name| current(name).is_none_or(|value| value.is_empty()))
}

/// What to add to `current` from `fresh` (UserEnvironment.cs:24-25): each fresh variable that is missing or empty now. A variable
/// the process already has keeps its value, so a launch with its own settings is never overridden.
pub fn missing(current: impl Fn(&str) -> Option<String>, fresh: &Variables) -> Vec<(String, String)> {
    fresh
        .iter()
        .filter(|(name, value)| !name.is_empty() && !value.is_empty() && current(name).is_none_or(|value| value.is_empty()))
        .map(|(name, value)| (name.to_string(), value.to_string()))
        .collect()
}

/// A `CreateEnvironmentBlock` block (`NAME=value\0…\0\0`) as a case-blind map (UserEnvironment.cs:31-41). Entries without a name
/// (the drive-letter `=C:=C:\` ones begin with `=`) are left out; a later duplicate wins.
pub fn parse(block: &str) -> Variables {
    let mut values = Variables::new();
    for entry in block.split('\0') {
        match entry.find('=') {
            Some(equals) if equals > 0 => values.set(&entry[..equals], &entry[equals + 1..]),
            _ => {}
        }
    }
    values
}

/// The user's fresh environment, or None when Windows will not build it (UserEnvironment.cs:44-69).
pub fn fresh() -> Option<Variables> {
    let mut token: HANDLE = std::ptr::null_mut();
    // SAFETY: this process's pseudo handle; `token` receives a query handle.
    if unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) } == 0 {
        return None;
    }
    let token = owned(token).ok()?;
    let mut block: *mut c_void = std::ptr::null_mut();
    // SAFETY: a token handle; `block` receives a block Windows allocates (no inheritance from this process).
    if unsafe { CreateEnvironmentBlock(&mut block, crate::win::raw(&token), 0) } == 0 || block.is_null() {
        return None;
    }
    // The block is UTF-16 strings, each ended by NUL, the whole ended by an empty one.
    let mut text = String::new();
    // SAFETY: walking a well-formed environment block Windows built, until its closing empty string.
    unsafe {
        let mut at = block as *const u16;
        loop {
            let mut length = 0usize;
            while *at.add(length) != 0 {
                length += 1;
            }
            if length == 0 {
                break;
            }
            text.push_str(&String::from_utf16_lossy(std::slice::from_raw_parts(at, length)));
            text.push('\0');
            at = at.add(length + 1);
        }
        DestroyEnvironmentBlock(block);
    }
    let values = parse(&text);
    (!values.is_empty()).then_some(values)
}

/// At startup: a process started with a bare environment (by an update helper that predates this fix, or any other launcher that
/// cleared it) takes the user's missing variables, so what it runs inherits a whole environment (UserEnvironment.cs:76-82).
/// Returns how many variables were added.
///
/// # Safety
/// It sets this process's environment: call it before any other thread runs (the start of `main`), as `std::env::set_var` needs.
pub unsafe fn repair_current_process() -> usize {
    let current = |name: &str| std::env::var(name).ok();
    if !is_bare(current) {
        return 0;
    }
    let Some(fresh) = fresh() else { return 0 };
    let missing = missing(current, &fresh);
    for (name, value) in &missing {
        // SAFETY: the caller guarantees no other thread reads or writes the environment now.
        unsafe { std::env::set_var(name, value) };
    }
    missing.len()
}
