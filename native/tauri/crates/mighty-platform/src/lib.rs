//! The ports between Mighty Code's shared logic and the operating system (docs/tauri-architecture.md §1–2).
//!
//! Every place the shared crates need the OS — start a process, open a pseudo console, read a secret, place a native view over the
//! page — is a trait here, with a written contract and a fake (`fake`) the shared tests run against, so they give the same result
//! on Windows and on the Mac. Each OS plugs in its own implementation (`mighty-platform-windows`, `mighty-platform-macos`);
//! `src-tauri/src/platform.rs` picks one by target, in one place.
//!
//! Rules (the architecture's): this crate has no `cfg(target_os)` and no OS call; a behaviour that differs on purpose between the
//! OSes is a named flag of [`PlatformProfile`], never an `if windows` in shared code; an operation an OS does not have (yet)
//! answers [`PlatformError::Unsupported`].

use std::future::Future;
use std::pin::Pin;
use std::sync::Arc;

pub mod clipboard;
pub mod console;
pub mod environment;
mod error;
pub mod fake;
pub mod files;
pub mod firewall;
pub mod fonts;
pub mod http;
pub mod notify;
pub mod pairing;
pub mod paths;
pub mod prefs;
pub mod process;
pub mod profile;
pub mod pty;
pub mod secrets;
pub mod surface;
pub mod unsupported;
pub mod update;

pub use clipboard::Clipboard;
pub use environment::{CliEnvironment, EnvironmentSource, ShellEnvironment};
pub use error::{PlatformError, Result};
pub use files::{ClipboardImage, Desktop, FileHandles, FilePorts, PathProbe, PdfPage, PdfRaster, SaveDialog, SaveRequest};
pub use firewall::{Firewall, FirewallChange, FirewallRule};
pub use notify::{ActivationHandler, Notification, Notifier};
pub use pairing::{PairedDevice, PairingIdentity, PairingStore, PushRegistration};
pub use paths::{AppPaths, Paths};
pub use prefs::{PrefValue, PrefsStore};
pub use process::{Captured, Child, ChildControl, ProcessHost, SpawnRequest, capture};
pub use profile::{
    AgentSignIn, CliUpdater, McpTransport, Os, PairingLayout, PathStyle, PlatformProfile, PluginListWhileReading, SettingsStore,
};
pub use pty::{PtyControl, PtyHost, PtyRequest, PtySession, PtySize, TerminalInput};
pub use secrets::SecretVault;
pub use surface::{
    BrowserEvent, BrowserListener, BrowserOptions, BrowserSurface, DeviceRect, Drawing, LogicalRect, NativeSurface, PetWindow,
    SurfaceFactory, SurfaceKind, SurfaceState, TerminalOptions, TerminalSurface,
};
pub use update::{StagedUpdate, UpdateInstaller};

/// A boxed future, so the ports stay usable as trait objects (`Arc<dyn ProcessHost>`).
pub type BoxFuture<'a, T> = Pin<Box<dyn Future<Output = T> + Send + 'a>>;

/// Every port one OS implements, as the shared logic receives them. The surfaces need the app's window and come separately
/// ([`SurfaceFactory`]).
#[derive(Clone)]
pub struct Platform {
    pub profile: PlatformProfile,
    pub paths: Arc<dyn Paths>,
    pub processes: Arc<dyn ProcessHost>,
    pub ptys: Arc<dyn PtyHost>,
    pub environment: Arc<dyn ShellEnvironment>,
    pub secrets: Arc<dyn SecretVault>,
    pub prefs: Arc<dyn PrefsStore>,
    pub pairing: Arc<dyn PairingStore>,
    pub notifier: Arc<dyn Notifier>,
    pub firewall: Arc<dyn Firewall>,
    pub updates: Arc<dyn UpdateInstaller>,
}

impl std::fmt::Debug for Platform {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Platform").field("os", &self.profile.os).field("data_dir", &self.paths.data_dir()).finish_non_exhaustive()
    }
}
