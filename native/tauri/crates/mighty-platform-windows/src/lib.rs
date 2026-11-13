//! The Windows implementation of the `mighty-platform` ports. On any other OS this crate is empty (everything is behind
//! `cfg(windows)`), so the workspace builds everywhere; `src-tauri/src/platform.rs` picks it on Windows.
//!
//! Real so far: [`paths`] (`%APPDATA%\MightyCode`), [`RegistryPrefs`] reads (`HKCU\Software\MightyCode`), [`DpapiVault`] (the C#
//! `DpapiRemoteKeyVault` format), and the browser surface ([`WebviewSurfaces`], a WebView2 child webview of the main window).
//! Every other port is `mighty_platform::unsupported::Unsupported` until its stage (processes and ConPTY: W1-C).

#[cfg(windows)]
mod browser;
#[cfg(windows)]
mod dpapi;
#[cfg(windows)]
mod prefs;

#[cfg(windows)]
pub use imp::*;

#[cfg(windows)]
mod imp {
    use std::path::PathBuf;
    use std::sync::Arc;

    use mighty_platform::unsupported::Unsupported;
    use mighty_platform::{AppPaths, Paths, Platform, PlatformProfile, Result, SurfaceFactory};

    pub use crate::browser::WebviewSurfaces;
    pub use crate::dpapi::{DpapiVault, FOLDER as REMOTE_KEYS_FOLDER, file_name as remote_key_file_name};
    pub use crate::prefs::RegistryPrefs;

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
            processes: Arc::new(Unsupported),
            ptys: Arc::new(Unsupported),
            environment: Arc::new(Unsupported),
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
