//! What C# reads from the OS it runs on and this crate is told instead (no `cfg(target_os)` in a shared crate): whether paths and
//! text follow Windows. The host sets it once from `PlatformProfile::path_style` ([`set_windows`]); by default it is the OS this
//! was built for, as `mighty_core::normalize::set_windows_paths` is.

use std::sync::atomic::{AtomicU8, Ordering};

use mighty_platform::{PathStyle, PlatformProfile};

static STYLE: AtomicU8 = AtomicU8::new(0);

/// Windows' rules (`Path.IsPathFullyQualified`, `Environment.NewLine` = CRLF, `Path.PathSeparator` = `;`) or the Mac's. Also sets
/// mighty-core's path rules, which the state's normalize reads.
pub fn set_windows(windows: bool) {
    STYLE.store(if windows { 1 } else { 2 }, Ordering::Relaxed);
    mighty_core::normalize::set_windows_paths(windows);
}

/// [`set_windows`] from a platform's profile.
pub fn follow(profile: &PlatformProfile) {
    set_windows(profile.path_style == PathStyle::Windows);
}

pub fn windows() -> bool {
    match STYLE.load(Ordering::Relaxed) {
        1 => true,
        2 => false,
        _ => cfg!(windows),
    }
}

/// `Environment.NewLine`, which `Utf8JsonWriter` writes between indented lines.
pub fn new_line() -> &'static str {
    if windows() { "\r\n" } else { "\n" }
}

/// `Path.PathSeparator`.
pub fn path_separator() -> char {
    if windows() { ';' } else { ':' }
}

/// `Path.Combine(a, b)` for a relative `b`.
pub fn combine(a: &str, b: &str) -> String {
    if a.is_empty() {
        return b.to_string();
    }
    let separator = if windows() { '\\' } else { '/' };
    if a.ends_with(separator) || a.ends_with('/') || windows() && a.ends_with(':') {
        format!("{a}{b}")
    } else {
        format!("{a}{separator}{b}")
    }
}
