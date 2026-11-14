//! The macOS implementation of the `mighty-platform` ports, written in Rust (docs/tauri-architecture.md §1 rule 4: AppKit through
//! objc2, the terminal through libghostty's C API, the browser through CEF or WKWebView). On any other OS this crate is empty.
//!
//! Real so far: [`paths`] (`~/Library/Application Support/MightyCode`). Every other port is
//! `mighty_platform::unsupported::Unsupported` until the Mac stage; the surfaces already say what the Mac draws natively (the
//! terminal: Ghostty; the browser: CEF or WKWebView) so the page reserves the right slots, but opening one is unsupported.

#[cfg(target_os = "macos")]
pub use imp::*;

#[cfg(target_os = "macos")]
mod imp {
    use std::path::PathBuf;
    use std::sync::Arc;

    use mighty_platform::unsupported::Unsupported;
    use mighty_platform::{
        AppPaths, BoxFuture, BrowserOptions, BrowserSurface, Drawing, PetWindow, Platform, PlatformError, PlatformProfile, Result,
        SurfaceFactory, SurfaceKind, TerminalOptions, TerminalSurface,
    };

    /// The data folder's name before the rename (Swift `StateRepository`).
    pub const LEGACY_FOLDERS: [&str; 1] = ["mighty-claude"];

    /// `~/Library/Application Support/MightyCode` (or `profile`), the pre-rename folder, and
    /// `~/Library/Application Support/<identifier>` for this machine's webview data.
    pub fn paths(profile: Option<PathBuf>, identifier: &str) -> Result<AppPaths> {
        AppPaths::resolve(profile, dirs::config_dir(), &LEGACY_FOLDERS, dirs::data_local_dir(), identifier)
    }

    pub fn platform(profile: Option<PathBuf>, identifier: &str) -> Result<Platform> {
        Ok(Platform {
            profile: PlatformProfile::macos(),
            paths: Arc::new(paths(profile, identifier)?),
            processes: Arc::new(Unsupported),
            ptys: Arc::new(Unsupported),
            environment: Arc::new(Unsupported),
            secrets: Arc::new(Unsupported),
            prefs: Arc::new(Unsupported),
            pairing: Arc::new(Unsupported),
            notifier: Arc::new(Unsupported),
            firewall: Arc::new(Unsupported),
            updates: Arc::new(Unsupported),
        })
    }

    /// The Mac's native views: both kinds are native (Ghostty; CEF or WKWebView by `browser.engineEnabled`), none written yet.
    pub struct MacSurfaces;

    impl SurfaceFactory for MacSurfaces {
        fn drawing(&self, _kind: SurfaceKind) -> Drawing {
            Drawing::Native
        }
        fn open_browser(&self, _options: BrowserOptions) -> BoxFuture<'_, Result<Box<dyn BrowserSurface>>> {
            Box::pin(async { Err(PlatformError::unsupported("SurfaceFactory", "open_browser")) })
        }
        fn open_terminal(&self, _options: TerminalOptions) -> BoxFuture<'_, Result<Box<dyn TerminalSurface>>> {
            Box::pin(async { Err(PlatformError::unsupported("SurfaceFactory", "open_terminal")) })
        }
        fn open_pet(&self) -> BoxFuture<'_, Result<Box<dyn PetWindow>>> {
            Box::pin(async { Err(PlatformError::unsupported("SurfaceFactory", "open_pet")) })
        }
    }

    /// The native views of `window` (its NSView will host Ghostty and the browser; unused until the Mac stage).
    /// An address handed to the default handler (`NSWorkspace.open`): not yet on the Mac.
    pub fn open_url(_url: &str) -> std::result::Result<(), String> {
        Err("opening addresses is not available on this platform yet".into())
    }

    /// A folder shown in Finder (`NSWorkspace.activateFileViewerSelecting`): not yet on the Mac.
    pub fn show_in_explorer(_folder: &std::path::Path) -> std::result::Result<(), String> {
        Err("showing a folder is not available on this platform yet".into())
    }

    /// The browser engine on the Mac is the system's (WKWebView) or the bundled CEF: always there.
    pub fn browser_runtime() -> Option<String> {
        Some("system".into())
    }

    /// The Mac has no engine to install: these are Windows' WebView2 bootstrapper steps.
    pub fn download_bootstrapper(_path: &std::path::Path) -> std::result::Result<(), String> {
        Err("not on this platform".into())
    }
    pub fn is_microsoft_signed(_path: &std::path::Path) -> bool {
        false
    }
    pub fn run_installer(_path: &std::path::Path) -> std::result::Result<i32, String> {
        Err("not on this platform".into())
    }

    /// The user's number format (`NSLocale.current`'s grouping): not yet on the Mac, which keeps a comma every three digits.
    pub fn number_grouping() -> Option<(String, Vec<usize>)> {
        None
    }

    /// The files pane's ports: not written yet on the Mac (W4 stub). A path is walked again after it is opened (C# off Windows), a
    /// PDF and the desktop's actions are refused.
    pub fn file_ports() -> mighty_platform::FilePorts {
        mighty_platform::FilePorts::unsupported()
    }

    /// What the Antigravity rule safety asks the disk (mighty-platform `PathProbe`, Swift `AntigravityRuleSafety`): a link read
    /// without following it, what the file system calls a path (`realpath`); the Mac has no drive letters.
    pub struct MacPathProbe;

    impl mighty_platform::PathProbe for MacPathProbe {
        fn checks_paths(&self) -> bool {
            true
        }
        fn fixed_drive(&self, _letter: char) -> bool {
            false
        }
        fn link_target(&self, path: &std::path::Path) -> Option<PathBuf> {
            let meta = std::fs::symlink_metadata(path).ok()?;
            if !meta.file_type().is_symlink() {
                return None;
            }
            std::fs::read_link(path).ok()
        }
        fn final_path(&self, path: &std::path::Path) -> Option<PathBuf> {
            std::fs::canonicalize(path).ok()
        }
    }

    /// The Antigravity rule safety's disk questions on the Mac.
    pub fn path_probe() -> Arc<dyn mighty_platform::PathProbe> {
        Arc::new(MacPathProbe)
    }

    /// The user's short date and time: not yet on the Mac.
    pub fn short_date_time(_at: std::time::SystemTime) -> Option<String> {
        None
    }

    /// The user's decimal separator: not yet on the Mac.
    pub fn decimal_separator() -> Option<String> {
        None
    }

    pub fn surfaces(_window: tauri::Window) -> Arc<dyn SurfaceFactory> {
        Arc::new(MacSurfaces)
    }

    /// The pasteboard (`NSPasteboard.general`): not yet on the Mac, whose terminal (Ghostty) copies and pastes by itself.
    pub fn clipboard() -> Arc<dyn mighty_platform::Clipboard> {
        Arc::new(Unsupported)
    }

    /// Each copy of the command `name` along the environment's PATH (M/AppStore+AskTargets.swift reads the login shell's PATH): an
    /// executable file of that name in each folder, once.
    pub fn cli_copies(name: &str, environment: &std::collections::BTreeMap<String, String>) -> Vec<String> {
        use std::os::unix::fs::PermissionsExt as _;
        let mut copies: Vec<String> = Vec::new();
        for folder in environment.get("PATH").map(String::as_str).unwrap_or("").split(':').take(64) {
            if folder.is_empty() || !folder.starts_with('/') {
                continue;
            }
            let path = std::path::Path::new(folder).join(name);
            let runnable = std::fs::metadata(&path).is_ok_and(|m| m.is_file() && m.permissions().mode() & 0o111 != 0);
            let text = path.to_string_lossy().into_owned();
            if runnable && !copies.contains(&text) {
                copies.push(text);
            }
        }
        copies
    }

    #[cfg(test)]
    mod tests {
        use super::*;
        use mighty_platform::Paths;

        #[test]
        fn the_default_profile_is_application_support_mightycode() {
            let paths = paths(None, "dev.mightycode.tauri").unwrap();
            assert!(paths.data_dir().ends_with("Library/Application Support/MightyCode"));
            assert!(paths.legacy_data_dirs()[0].ends_with("Library/Application Support/mighty-claude"));
            assert_eq!(MacSurfaces.drawing(SurfaceKind::Terminal), Drawing::Native);
        }
    }
}
