//! The one place the host chooses the OS's implementation of the ports (docs/tauri-architecture.md §1, rule 3): Windows builds
//! use `mighty-platform-windows`, Mac builds `mighty-platform-macos`. Nothing else in the host names either crate or asks which
//! OS it runs on; what differs on purpose reaches the shared logic as `Platform::profile`.

use std::path::PathBuf;
use std::sync::Arc;

use mighty_platform::{FontCatalog, HttpsGet, Platform, Result, SurfaceFactory, TerminalLauncher};

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

/// W6's ports, managed by the app (`host.app.state::<Ports>()`): a terminal window that outlives the app (sign-ins, the setup
/// screen's install), the installed font families (Settings › 글자) and the account usage's HTTPS GET. A smoke gets the recording
/// fakes for the two that reach outside the app (no window opens, no request leaves), and the real font list, which only reads.
#[allow(dead_code, reason = "the W6 parts read them: W6-B and W6-C the terminal window, W6-C the HTTPS GET")]
pub struct Ports {
    pub terminals: Arc<dyn TerminalLauncher>,
    pub fonts: Arc<dyn FontCatalog>,
    pub https: Arc<dyn HttpsGet>,
    /// The completion notification (a smoke's records them: no toast shows).
    pub notifier: Arc<dyn mighty_platform::Notifier>,
}

pub fn ports(smoke: bool) -> Ports {
    if smoke {
        Ports {
            terminals: Arc::new(mighty_platform::fake::RecordingTerminalLauncher::default()),
            fonts: os::font_catalog(),
            https: Arc::new(mighty_platform::fake::RecordingHttps::default()),
            notifier: Arc::new(mighty_platform::fake::RecordingNotifier::default()),
        }
    } else {
        Ports { terminals: os::terminal_launcher(), fonts: os::font_catalog(), https: os::https_get(), notifier: os::notifier() }
    }
}

/// C# `DateTimeOffset.TryParse(updatedAt) … ToLocalTime().ToString("T", CurrentCulture)`: a reported time in the user's long time
/// format (the session info popover's "received"); none when it does not read or the OS does not say.
pub fn long_time(at: &str) -> Option<String> {
    let parsed = chrono::DateTime::parse_from_rfc3339(at).ok()?;
    let system = std::time::SystemTime::UNIX_EPOCH + std::time::Duration::from_millis(u64::try_from(parsed.timestamp_millis()).ok()?);
    os::long_time(system)
}

/// The setup screen's files port (setup.rs, W6-B): the script folder and its ACL, and whether the app runs elevated (Windows'
/// `WindowsSetupFiles`; the Mac `Unsupported`).
#[cfg(windows)]
#[allow(dead_code, reason = "setup.rs's files_port (W6-B) switches to it at integration")]
pub fn setup_files() -> Arc<dyn mighty_platform::SetupFiles> {
    Arc::new(os::WindowsSetupFiles)
}

#[cfg(target_os = "macos")]
#[allow(dead_code, reason = "setup.rs's files_port (W6-B) switches to it at integration")]
pub fn setup_files() -> Arc<dyn mighty_platform::SetupFiles> {
    Arc::new(mighty_platform::unsupported::Unsupported)
}

/// Registers the app for its notifications with the OS (Windows: the AUMID under HKCU); the caller decides whether it may write.
pub fn register_notifications() -> std::result::Result<(), String> {
    os::register_notifications()
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

/// What the Antigravity rule safety asks the disk about a path agy named (a link, what the file system calls it, a fixed drive).
pub fn path_probe() -> Arc<dyn mighty_platform::PathProbe> {
    os::path_probe()
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
