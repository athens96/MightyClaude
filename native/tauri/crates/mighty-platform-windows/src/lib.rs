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

        #[test]
        fn the_default_profile_is_appdata_mightycode() {
            let paths = paths(None, "dev.mightycode.tauri").unwrap();
            assert_eq!(paths.data_dir(), dirs::config_dir().unwrap().join("MightyCode"));
            assert_eq!(paths.legacy_data_dirs().len(), 2);
            assert_eq!(paths.local_dir(), dirs::data_local_dir().unwrap().join("dev.mightycode.tauri"));
        }
    }
}
