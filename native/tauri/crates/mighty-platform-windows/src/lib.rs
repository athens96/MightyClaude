//! The Windows implementation of the `mighty-platform` ports. On any other OS this crate is empty (everything is behind
//! `cfg(windows)`), so the workspace builds everywhere; `src-tauri/src/platform.rs` picks it on Windows.
//!
//! Real so far: [`paths`] (`%APPDATA%\MightyCode`), [`RegistryPrefs`] reads (`HKCU\Software\MightyCode`), [`DpapiVault`] (the C#
//! `DpapiRemoteKeyVault` format), the browser surface ([`WebviewSurfaces`], a WebView2 child webview of the main window), and the
//! process layer (W1-C, the C# `ChildProcess`, `WindowsCommand`, `CliRunner`, `CliDiscovery`, `CliEnvironment`,
//! `WindowsShellEnvironment`, `UserEnvironment`, `PseudoTerminal` and `ShellActivity`): [`WindowsProcessHost`],
//! [`WindowsPtyHost`], [`WindowsShellEnvironment`]. Every other port is `mighty_platform::unsupported::Unsupported` until its stage.

#[cfg(windows)]
mod browser;
#[cfg(windows)]
pub mod cli;
#[cfg(windows)]
pub mod command;
#[cfg(windows)]
pub mod discovery;
#[cfg(windows)]
mod dpapi;
#[cfg(windows)]
pub mod environment;
#[cfg(windows)]
pub mod files;
#[cfg(windows)]
mod prefs;
#[cfg(windows)]
mod process;
#[cfg(windows)]
pub mod pty;
#[cfg(windows)]
pub mod shell_activity;
#[cfg(windows)]
mod text;
#[cfg(windows)]
pub mod user_environment;
#[cfg(windows)]
mod variables;
#[cfg(windows)]
mod win;

#[cfg(windows)]
pub use imp::*;

#[cfg(windows)]
mod imp {
    use std::path::PathBuf;
    use std::sync::Arc;

    use mighty_platform::unsupported::Unsupported;
    use mighty_platform::{AppPaths, Paths, Platform, PlatformProfile, Result, SurfaceFactory};

    pub use crate::browser::WebviewSurfaces;
    pub use crate::cli::{CliRunResult, CliRunner};
    pub use crate::dpapi::{DpapiVault, FOLDER as REMOTE_KEYS_FOLDER, file_name as remote_key_file_name};
    pub use crate::environment::{CliEnvironmentSnapshot, WindowsShellEnvironment};
    pub use crate::files::{WindowsDesktop, WindowsFileHandles, WindowsPdf, decimal_separator, file_ports, short_date_time};
    pub use crate::prefs::RegistryPrefs;
    pub use crate::process::{KILLED_EXIT_CODE, WindowsProcessHost};
    pub use crate::pty::WindowsPtyHost;
    pub use crate::variables::Variables;

    /// The folders of the app before its rename, under `%APPDATA%` (C# `Program.cs`, `RenameMigration`).
    pub const LEGACY_FOLDERS: [&str; 2] = ["MightyClaude", "mighty-claude"];

    /// `%APPDATA%\MightyCode` (or `profile`), the pre-rename folders, and `%LOCALAPPDATA%\<identifier>`.
    pub fn paths(profile: Option<PathBuf>, identifier: &str) -> Result<AppPaths> {
        AppPaths::resolve(profile, dirs::config_dir(), &LEGACY_FOLDERS, dirs::data_local_dir(), identifier)
    }

    /// C# `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })`: an http(s) or mailto address handed to the user's
    /// default handler through `ShellExecuteW` (the handler is never the app's child, so it outlives the app).
    pub fn open_url(url: &str) -> std::result::Result<(), String> {
        use std::os::windows::ffi::OsStrExt;
        use windows_sys::Win32::UI::Shell::ShellExecuteW;
        use windows_sys::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;
        let wide = |text: &str| std::ffi::OsStr::new(text).encode_wide().chain(std::iter::once(0)).collect::<Vec<u16>>();
        let (verb, target) = (wide("open"), wide(url));
        // SAFETY: two NUL-terminated strings that live across the call; the other pointers are null as documented.
        let done = unsafe {
            ShellExecuteW(std::ptr::null_mut(), verb.as_ptr(), target.as_ptr(), std::ptr::null(), std::ptr::null(), SW_SHOWNORMAL)
        };
        // ShellExecute's result is greater than 32 when it succeeded.
        if done as isize > 32 { Ok(()) } else { Err(std::io::Error::last_os_error().to_string()) }
    }

    /// The number format C#'s `CultureInfo.CurrentCulture` gives `{value:N0}`: the user's regional format with their own changes
    /// (`GetLocaleInfoEx` of the user's default locale): its group separator, and `LOCALE_SGROUPING` as .NET's `NumberGroupSizes`
    /// ("3;0" is `[3]`, India's "3;2;0" `[3, 2]`, "3" alone `[3, 0]`). None when Windows does not say.
    pub fn number_grouping() -> Option<(String, Vec<usize>)> {
        use windows_sys::Win32::Globalization::{GetLocaleInfoEx, LOCALE_SGROUPING, LOCALE_STHOUSAND};
        let read = |kind: u32| -> Option<String> {
            let mut buffer = [0u16; 32];
            // SAFETY: a null name is LOCALE_NAME_USER_DEFAULT; the buffer and its length in UTF-16 units, NUL included.
            let written = unsafe { GetLocaleInfoEx(std::ptr::null(), kind, buffer.as_mut_ptr(), buffer.len() as i32) };
            (written > 0).then(|| String::from_utf16_lossy(&buffer[..written as usize - 1]))
        };
        let separator = read(LOCALE_STHOUSAND)?;
        let mut sizes: Vec<usize> = read(LOCALE_SGROUPING)?.split(';').map(|size| size.trim().parse().ok()).collect::<Option<_>>()?;
        match sizes.last() {
            // A trailing 0 repeats the size before it, as .NET reads it; without one the grouping stops after the sizes given.
            Some(0) if sizes.len() > 1 => {
                sizes.pop();
            }
            Some(_) => sizes.push(0),
            None => return None,
        }
        Some((separator, sizes))
    }

    /// Every port for this run.
    pub fn platform(profile: Option<PathBuf>, identifier: &str) -> Result<Platform> {
        let paths = paths(profile, identifier)?;
        let vault = DpapiVault::new(paths.data_dir().to_path_buf());
        Ok(Platform {
            profile: PlatformProfile::windows(),
            paths: Arc::new(paths),
            processes: Arc::new(WindowsProcessHost::new()),
            ptys: Arc::new(WindowsPtyHost::new()),
            environment: Arc::new(WindowsShellEnvironment::new()),
            secrets: Arc::new(vault),
            prefs: Arc::new(RegistryPrefs::app()),
            pairing: Arc::new(Unsupported),
            notifier: Arc::new(Unsupported),
            firewall: Arc::new(Unsupported),
            updates: Arc::new(Unsupported),
        })
    }

    /// The native views of `window`: browsers are WebView2 child webviews; the terminal is the page's (xterm.js).
    pub fn surfaces(window: tauri::Window) -> Arc<dyn SurfaceFactory> {
        Arc::new(WebviewSurfaces::new(window))
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        /// The user's number format reads as .NET's `NumberGroupSizes`: a list whose sizes are its groups from the right.
        #[test]
        fn the_user_number_format_reads() {
            let (separator, sizes) = number_grouping().expect("Windows names the user's number format");
            assert!(separator.encode_utf16().count() <= 4, "a separator, not a sentence: {separator:?}");
            assert!(!sizes.is_empty() && sizes.iter().all(|size| *size <= 9), "group sizes {sizes:?}");
        }

        #[test]
        fn the_default_profile_is_appdata_mightycode() {
            let paths = paths(None, "dev.mightycode.tauri").unwrap();
            assert_eq!(paths.data_dir(), dirs::config_dir().unwrap().join("MightyCode"));
            assert_eq!(paths.legacy_data_dirs().len(), 2);
            assert_eq!(paths.local_dir(), dirs::data_local_dir().unwrap().join("dev.mightycode.tauri"));
        }
    }
}
