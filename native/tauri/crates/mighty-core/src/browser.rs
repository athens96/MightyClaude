//! The manual browser pane's rules (C# `BrowserHistory`, `BrowserNavigationState`, `BrowserProfile`, `BrowserEngineService`,
//! MightyCode.Core/Browser*.cs; the Mac's BrowserHistory): back and forward as Chromium keeps them, one web profile per workspace, and
//! the engine's state and its install, which downloads nothing until it is asked to. Address resolution (`BrowserAddress.Resolve`)
//! parses URLs and lives with the host.

use std::path::{Path, PathBuf};

use crate::locale::Locale;

/// `BrowserNavigationState`.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct NavigationState {
    pub can_go_back: bool,
    pub can_go_forward: bool,
    pub is_loading: bool,
    pub url: Option<String>,
}

/// What `Uri.Equals` compares: the address without its user information and its fragment.
fn same_address(a: &str, b: &str) -> bool {
    fn key(url: &str) -> &str {
        url.split('#').next().unwrap_or(url)
    }
    fn without_user(url: &str) -> String {
        let url = key(url);
        match url.split_once("://") {
            Some((scheme, rest)) => {
                let authority_end = rest.find(['/', '?']).unwrap_or(rest.len());
                let (authority, tail) = rest.split_at(authority_end);
                let host = authority.rsplit_once('@').map_or(authority, |(_, host)| host);
                format!("{scheme}://{host}{tail}")
            }
            None => url.to_string(),
        }
    }
    without_user(a) == without_user(b)
}

/// `BrowserHistory`: one browser pane's back and forward. A visit of the address shown is a reload; a new visit drops the forward
/// entries (Chromium's rule, the Mac's).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct History {
    entries: Vec<String>,
    /// The entry shown; none before the first visit.
    index: Option<usize>,
}

impl History {
    pub fn current(&self) -> Option<&str> {
        self.index.and_then(|at| self.entries.get(at)).map(String::as_str)
    }

    pub fn can_go_back(&self) -> bool {
        self.index.is_some_and(|at| at > 0)
    }

    pub fn can_go_forward(&self) -> bool {
        self.index.is_some_and(|at| at + 1 < self.entries.len())
    }

    /// `Visit`.
    pub fn visit(&mut self, url: &str) {
        if self.current().is_some_and(|current| same_address(current, url)) {
            return;
        }
        if let Some(at) = self.index {
            self.entries.truncate(at + 1);
        }
        self.entries.push(url.to_string());
        self.index = Some(self.entries.len() - 1);
    }

    /// `GoBack`: the entry it went back to, or none at the first.
    pub fn go_back(&mut self) -> Option<&str> {
        if !self.can_go_back() {
            return None;
        }
        self.index = self.index.map(|at| at - 1);
        self.current()
    }

    /// `GoForward`.
    pub fn go_forward(&mut self) -> Option<&str> {
        if !self.can_go_forward() {
            return None;
        }
        self.index = self.index.map(|at| at + 1);
        self.current()
    }

    /// `State(isLoading)`.
    pub fn state(&self, is_loading: bool) -> NavigationState {
        NavigationState {
            can_go_back: self.can_go_back(),
            can_go_forward: self.can_go_forward(),
            is_loading,
            url: self.current().map(str::to_string),
        }
    }
}

/// `BrowserProfile.ProfileFolder`: a workspace's web data, `<root>/browser-profiles/<key>`; a different root (another `--profile`) gives
/// another folder.
pub fn profile_folder(root: &Path, workspace_profile_key: &str) -> PathBuf {
    root.join("browser-profiles").join(workspace_profile_key)
}

/// `BrowserEngineState` as the page's browser state names it.
pub fn engine_status(enabled: bool, runtime_version: Option<&str>) -> &'static str {
    match (enabled, runtime_version) {
        (false, _) => "disabled",
        (true, None) => "missing",
        (true, Some(_)) => "ready",
    }
}

/// `IBrowserInstaller`: the Microsoft per-user bootstrapper downloaded, its signature checked, run (the OS's).
pub trait BrowserInstaller {
    fn download(&self, path: &Path) -> Result<(), String>;
    fn verify_microsoft_signature(&self, path: &Path) -> bool;
    fn run(&self, path: &Path) -> Result<i32, String>;
}

/// `BrowserInstallResult`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InstallResult {
    pub success: bool,
    pub error: Option<String>,
}

/// The bootstrapper's name in the temporary folder.
pub const BOOTSTRAPPER: &str = "MicrosoftEdgeWebview2Setup.exe";

/// `BrowserEngineService.InstallAsync`: download, check the Microsoft signature, run; the download is deleted whatever happened.
pub fn install(temp_dir: &Path, installer: &dyn BrowserInstaller, locale: &Locale) -> InstallResult {
    let file = temp_dir.join(BOOTSTRAPPER);
    let failed = |key: &str| InstallResult { success: false, error: Some(locale.get(key)) };
    let result = match installer.download(&file) {
        Err(_) => failed("browser.runtime.installFailed"),
        Ok(()) if !installer.verify_microsoft_signature(&file) => failed("browser.runtime.signatureFailed"),
        Ok(()) => match installer.run(&file) {
            Ok(0) => InstallResult { success: true, error: None },
            Ok(_) | Err(_) => failed("browser.runtime.installFailed"),
        },
    };
    if file.exists() {
        let _ = std::fs::remove_file(&file);
    }
    result
}

#[cfg(test)]
mod tests {
    use std::cell::Cell;

    use super::*;

    /// `BrowserVerification.HistoryFollowsMacOSRules`.
    #[test]
    fn history_follows_the_mac_rules() {
        let (a, b, c) = ("https://example.com/", "https://example.com/second", "https://example.com/third");
        let mut history = History::default();
        assert_eq!(history.state(false), NavigationState::default(), "empty state");
        history.visit(a);
        let state = history.state(true);
        assert_eq!(state.url.as_deref(), Some(a));
        assert!(state.is_loading && !state.can_go_back && !state.can_go_forward);
        history.visit(a);
        assert!(!history.can_go_back(), "a reload must not grow history");
        history.visit(b);
        let state = history.state(false);
        assert_eq!(state.url.as_deref(), Some(b));
        assert!(state.can_go_back && !state.can_go_forward && !state.is_loading);
        assert_eq!(history.go_back(), Some(a));
        assert!(!history.can_go_back() && history.can_go_forward());
        assert_eq!(history.go_forward(), Some(b));
        assert_eq!(history.go_forward(), None, "forward returns nothing at the end");
        assert_eq!(history.go_back(), Some(a));
        history.visit(c);
        assert_eq!(history.current(), Some(c));
        assert!(history.can_go_back() && !history.can_go_forward(), "a new visit drops the forward tail");
        assert_eq!(history.go_back(), Some(a));
        assert_eq!(history.go_back(), None, "never back past the first page");
    }

    #[test]
    fn a_visit_that_differs_only_in_its_fragment_is_a_reload() {
        let mut history = History::default();
        history.visit("https://example.com/page#one");
        history.visit("https://example.com/page#two");
        history.visit("https://user:pw@example.com/page");
        assert!(!history.can_go_back(), "Uri.Equals ignores the fragment and the user information");
        history.visit("https://example.com/other");
        assert!(history.can_go_back());
    }

    /// `BrowserVerification.ProfileFolderPerWorkspaceFollowsProfile`.
    #[test]
    fn the_profile_folder_is_per_workspace_and_follows_the_profile() {
        let root = std::env::temp_dir().join("browser-profile-test");
        let (one, two) = (profile_folder(&root, "workspace-abc"), profile_folder(&root, "workspace-xyz"));
        assert_ne!(one, two);
        assert!(one.ends_with("workspace-abc") && two.ends_with("workspace-xyz"));
        assert_eq!(one.parent(), two.parent(), "a shared parent folder");
        assert!(one.starts_with(&root));
        assert_ne!(profile_folder(&root.with_extension("profile2"), "workspace-abc"), one, "--profile isolates the folder");
    }

    /// `BrowserVerification.RuntimeMissingOffersInstallOnlyOnClick`.
    #[test]
    fn the_engine_state_follows_the_setting_and_the_runtime() {
        assert_eq!(engine_status(true, None), "missing");
        assert_eq!(engine_status(false, None), "disabled");
        assert_eq!(engine_status(false, Some("130.0")), "disabled");
        assert_eq!(engine_status(true, Some("130.0")), "ready");
    }

    type Download = Box<dyn Fn(&Path) -> Result<(), String>>;

    struct Fake {
        download: Download,
        signed: bool,
        exit: i32,
        downloaded: Cell<bool>,
    }

    impl BrowserInstaller for Fake {
        fn download(&self, path: &Path) -> Result<(), String> {
            self.downloaded.set(true);
            (self.download)(path)
        }
        fn verify_microsoft_signature(&self, _path: &Path) -> bool {
            self.signed
        }
        fn run(&self, _path: &Path) -> Result<i32, String> {
            Ok(self.exit)
        }
    }

    fn writes(path: &Path) -> Result<(), String> {
        std::fs::write(path, "fake-exe").map_err(|e| e.to_string())
    }

    /// `InstallerRequiresMicrosoftSignature`, `InstallerFailureCleansUp`.
    #[test]
    fn the_install_needs_a_microsoft_signature_and_leaves_nothing_behind() {
        let en = Locale::new("en");
        let temp = std::env::temp_dir().join(format!("mighty-browser-install-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&temp).unwrap();
        let unsigned = Fake { download: Box::new(writes), signed: false, exit: 0, downloaded: Cell::new(false) };
        let result = install(&temp, &unsigned, &en);
        assert!(unsigned.downloaded.get() && !result.success);
        assert_eq!(result.error, Some(en.get("browser.runtime.signatureFailed")));
        assert!(!temp.join(BOOTSTRAPPER).exists(), "cleaned up after the signature failed");
        let failing = Fake { download: Box::new(writes), signed: true, exit: 1, downloaded: Cell::new(false) };
        assert_eq!(install(&temp, &failing, &en).error, Some(en.get("browser.runtime.installFailed")));
        assert!(!temp.join(BOOTSTRAPPER).exists(), "cleaned up after the installer failed");
        let broken = Fake {
            download: Box::new(|path| {
                writes(path)?;
                Err("download failed".into())
            }),
            signed: true,
            exit: 0,
            downloaded: Cell::new(false),
        };
        assert!(!install(&temp, &broken, &en).success);
        assert!(!temp.join(BOOTSTRAPPER).exists(), "cleaned up after the download failed");
        let good = Fake { download: Box::new(writes), signed: true, exit: 0, downloaded: Cell::new(false) };
        assert_eq!(install(&temp, &good, &en), InstallResult { success: true, error: None });
        std::fs::remove_dir_all(&temp).unwrap();
    }
}
