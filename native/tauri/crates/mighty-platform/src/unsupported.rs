//! A port an OS has not written yet: every operation answers [`PlatformError::Unsupported`], except where the port's contract
//! says it never fails (the CLI environment falls back to this process's, a missing secret is None). The OS crates hand this out
//! until their stage fills the port in, so the shared logic sees one typed refusal instead of a panic.

use std::path::{Path, PathBuf};

use crate::notify::ActivationHandler;
use crate::*;

/// Every port, unsupported.
#[derive(Debug, Clone, Copy, Default)]
pub struct Unsupported;

fn no<T>(port: &'static str, operation: &'static str) -> Result<T> {
    Err(PlatformError::unsupported(port, operation))
}

impl ProcessHost for Unsupported {
    fn spawn(&self, _request: SpawnRequest) -> BoxFuture<'_, Result<Child>> {
        Box::pin(async { no("ProcessHost", "spawn") })
    }
}

impl PtyHost for Unsupported {
    /// None known: an empty path.
    fn default_shell(&self) -> PathBuf {
        PathBuf::new()
    }
    fn shell_arguments(&self, _shell: &Path, _command: Option<&str>, _no_profile: bool) -> Vec<String> {
        vec![]
    }
    fn open(&self, _request: PtyRequest) -> BoxFuture<'_, Result<PtySession>> {
        Box::pin(async { no("PtyHost", "open") })
    }
}

/// The process's own environment, as a failed capture gives.
impl ShellEnvironment for Unsupported {
    fn resolve(&self, _workspace: Option<&Path>, _force: bool) -> BoxFuture<'_, CliEnvironment> {
        Box::pin(async { self.current() })
    }
    fn current(&self) -> CliEnvironment {
        CliEnvironment { values: std::env::vars().collect(), source: EnvironmentSource::ProcessFallback }
    }
    fn invalidate(&self, _workspace: Option<&Path>) {}
}

impl SecretVault for Unsupported {
    fn load(&self, _id: &str) -> Option<String> {
        None
    }
    fn save(&self, _id: &str, _secret: &str) -> Result<()> {
        no("SecretVault", "save")
    }
    fn delete(&self, _id: &str) -> Result<()> {
        no("SecretVault", "delete")
    }
}

impl PrefsStore for Unsupported {
    fn get(&self, _key: &str) -> Result<Option<PrefValue>> {
        no("PrefsStore", "get")
    }
    fn set(&self, _key: &str, _value: PrefValue) -> Result<()> {
        no("PrefsStore", "set")
    }
    fn remove(&self, _key: &str) -> Result<()> {
        no("PrefsStore", "remove")
    }
}

impl PairingStore for Unsupported {
    fn load(&self) -> Result<Option<PairingIdentity>> {
        no("PairingStore", "load")
    }
    fn save(&self, _identity: &PairingIdentity) -> Result<()> {
        no("PairingStore", "save")
    }
}

impl Notifier for Unsupported {
    fn notify(&self, _notification: Notification) -> BoxFuture<'_, Result<()>> {
        Box::pin(async { no("Notifier", "notify") })
    }
    fn on_activated(&self, _handler: ActivationHandler) {}
}

impl Firewall for Unsupported {
    fn read(&self) -> BoxFuture<'_, Result<FirewallRule>> {
        Box::pin(async { no("Firewall", "read") })
    }
    fn apply(&self, _ports: Vec<u16>) -> BoxFuture<'_, Result<FirewallChange>> {
        Box::pin(async { no("Firewall", "apply") })
    }
}

impl Clipboard for Unsupported {
    fn read_text(&self) -> Result<Option<String>> {
        no("Clipboard", "read_text")
    }
    fn write_text(&self, _text: &str) -> Result<()> {
        no("Clipboard", "write_text")
    }
}

impl UpdateInstaller for Unsupported {
    fn install_location(&self) -> Option<PathBuf> {
        None
    }
    fn hand_off(&self, _update: &StagedUpdate, _relaunch: bool) -> Result<()> {
        no("UpdateInstaller", "hand_off")
    }
}

/// Opens nothing; says the page draws the terminal and the platform the browser (so the page reserves the slot).
impl SurfaceFactory for Unsupported {
    fn drawing(&self, kind: SurfaceKind) -> Drawing {
        match kind {
            SurfaceKind::Terminal => Drawing::Web,
            SurfaceKind::Browser => Drawing::Native,
        }
    }
    fn open_browser(&self, _options: BrowserOptions) -> BoxFuture<'_, Result<Box<dyn BrowserSurface>>> {
        Box::pin(async { no("SurfaceFactory", "open_browser") })
    }
    fn open_terminal(&self, _options: TerminalOptions) -> BoxFuture<'_, Result<Box<dyn TerminalSurface>>> {
        Box::pin(async { no("SurfaceFactory", "open_terminal") })
    }
    fn open_pet(&self) -> BoxFuture<'_, Result<Box<dyn PetWindow>>> {
        Box::pin(async { no("SurfaceFactory", "open_pet") })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn every_operation_is_a_typed_refusal() {
        let port = Unsupported;
        assert!(SecretVault::save(&port, "id", "x").unwrap_err().is_unsupported());
        assert_eq!(SecretVault::load(&port, "id"), None);
        assert!(PrefsStore::get(&port, "k").unwrap_err().is_unsupported());
        assert!(Firewall::read(&port).await.unwrap_err().is_unsupported());
        assert_eq!(ShellEnvironment::resolve(&port, None, false).await.source, EnvironmentSource::ProcessFallback);
        assert!(port.read_text().unwrap_err().is_unsupported() && port.write_text("x").unwrap_err().is_unsupported());
        assert!(
            port.open_browser(BrowserOptions {
                pane_id: "p".into(),
                url: "about:blank".into(),
                data_dir: PathBuf::new(),
                bounds: DeviceRect::default()
            })
            .await
            .err()
            .is_some_and(|e| e.is_unsupported())
        );
    }
}
