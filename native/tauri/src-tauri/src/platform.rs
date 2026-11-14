//! The one place the host chooses the OS's implementation of the ports (docs/tauri-architecture.md §1, rule 3): Windows builds
//! use `mighty-platform-windows`, Mac builds `mighty-platform-macos`. Nothing else in the host names either crate or asks which
//! OS it runs on; what differs on purpose reaches the shared logic as `Platform::profile`.

use std::path::PathBuf;
use std::sync::Arc;

use mighty_platform::{Platform, Result, SurfaceFactory};

#[cfg(windows)]
use mighty_platform_windows as os;

#[cfg(target_os = "macos")]
use mighty_platform_macos as os;

#[cfg(not(any(windows, target_os = "macos")))]
compile_error!("Mighty Code's host builds for Windows and macOS only.");

/// Every port of this OS; `profile` is `--profile`, `identifier` the app's (tauri.conf.json), which names its local data folder.
pub fn platform(profile: Option<PathBuf>, identifier: &str) -> Result<Platform> {
    os::platform(profile, identifier)
}

/// The native views over `window`'s page.
pub fn surfaces(window: tauri::Window) -> Arc<dyn SurfaceFactory> {
    os::surfaces(window)
}

/// The system clipboard's text (a terminal's copy and paste).
pub fn clipboard() -> Arc<dyn mighty_platform::Clipboard> {
    os::clipboard()
}

/// An http(s) or mailto address opened with the user's default handler (C# `Process.Start` with `UseShellExecute`).
pub fn open_url(url: &str) -> std::result::Result<(), String> {
    os::open_url(url)
}

/// The user's number format (C# `CultureInfo.CurrentCulture`'s group separator and sizes), when the OS says.
pub fn number_grouping() -> Option<(String, Vec<usize>)> {
    os::number_grouping()
}

/// The files pane's ports (an open file's handle, a PDF's first page, show in the file manager, open with the default app, the
/// clipboard).
pub fn file_ports() -> mighty_platform::FilePorts {
    os::file_ports()
}

/// C# `ToLocalTime().ToString("g", CultureInfo.CurrentCulture)`, when the OS says.
pub fn short_date_time(at: std::time::SystemTime) -> Option<String> {
    os::short_date_time(at)
}

/// The user's decimal separator, when the OS says.
pub fn decimal_separator() -> Option<String> {
    os::decimal_separator()
}

/// A workspace's folder opened in the OS's file manager (C# `ShowWorkspaceInExplorer`).
pub fn show_in_explorer(folder: &std::path::Path) -> std::result::Result<(), String> {
    os::show_in_explorer(folder)
}

/// Each copy of the command `name` this computer has along `environment`'s PATH (C# `SetupProber.Executables`: with PATHEXT and the
/// installers' folders on Windows).
pub fn cli_copies(name: &str, environment: &std::collections::BTreeMap<String, String>) -> Vec<String> {
    os::cli_copies(name, environment)
}

/// The browser engine's installed version (C# `WebView2RuntimeLocator`), or none when the pane must offer its install.
pub fn browser_runtime() -> Option<String> {
    os::browser_runtime()
}

/// The browser engine's installer (C# `WebView2BootstrapperInstaller`), for mighty-core's `browser::install`.
pub struct BrowserInstaller;

impl mighty_core::browser::BrowserInstaller for BrowserInstaller {
    fn download(&self, path: &std::path::Path) -> std::result::Result<(), String> {
        os::download_bootstrapper(path)
    }
    fn verify_microsoft_signature(&self, path: &std::path::Path) -> bool {
        os::is_microsoft_signed(path)
    }
    fn run(&self, path: &std::path::Path) -> std::result::Result<i32, String> {
        os::run_installer(path)
    }
}
