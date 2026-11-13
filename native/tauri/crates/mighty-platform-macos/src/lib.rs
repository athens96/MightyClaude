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
    pub fn surfaces(_window: tauri::Window) -> Arc<dyn SurfaceFactory> {
        Arc::new(MacSurfaces)
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
