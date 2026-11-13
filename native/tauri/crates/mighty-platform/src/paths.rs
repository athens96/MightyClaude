//! Where the app keeps its files. The roots differ by OS; the layout under them is the same.
//!
//! | | Windows | Mac |
//! |---|---|---|
//! | data (roams, holds the state) | `%APPDATA%\MightyCode` | `~/Library/Application Support/MightyCode` |
//! | before the rename (read once) | `%APPDATA%\MightyClaude`, `%APPDATA%\mighty-claude` | `~/Library/Application Support/mighty-claude` |
//! | local (this machine only) | `%LOCALAPPDATA%\<identifier>` | `~/Library/Application Support/<identifier>` |
//!
//! `--profile <folder>` replaces the data folder, never reads the pre-rename ones, and gets webview and browser data of its own
//! under the local folder (named after the profile path), so two profiles never share a WebView2 folder.

use std::path::{Path, PathBuf};

use crate::{PlatformError, Result};

/// The state file's name in the data folder.
pub const STATE_FILE: &str = "workspace-state.json";

/// The app's folders. Implementors give the three roots; every other path is derived the same way on every OS.
pub trait Paths: Send + Sync {
    /// The data folder: `workspace-state.json` and its `.bak`, `delegation-state.json`, `remote-keys`, `image-cache`, ... It is
    /// `--profile` when one was given.
    fn data_dir(&self) -> &Path;
    /// The data folders of the app before its rename, whose state a default profile without one copies (empty with `--profile`).
    fn legacy_data_dirs(&self) -> &[PathBuf];
    /// The machine-local folder (never roamed): WebView2 data, browser profiles, caches.
    fn local_dir(&self) -> &Path;
    /// The folder name this profile's local data goes under: `default`, or the profile path made a folder name.
    fn profile_key(&self) -> &str;

    fn state_file(&self) -> PathBuf {
        self.data_dir().join(STATE_FILE)
    }
    fn state_backup(&self) -> PathBuf {
        self.data_dir().join(format!("{STATE_FILE}.bak"))
    }
    /// The Rust host's logs (`host.log`, `unimplemented.json`).
    fn logs_dir(&self) -> PathBuf {
        self.data_dir().join("tauri-logs")
    }
    fn image_cache_dir(&self) -> PathBuf {
        self.data_dir().join("image-cache")
    }
    fn file_originals_dir(&self) -> PathBuf {
        self.data_dir().join("file-originals")
    }
    /// The page's WebView2 / WKWebView data for this profile.
    fn webview_dir(&self) -> PathBuf {
        self.local_dir().join("webview").join(self.profile_key())
    }
    /// The browser panes' web data for this profile (one profile per workspace under it is W4's).
    fn browser_dir(&self) -> PathBuf {
        self.local_dir().join("browser").join(self.profile_key())
    }
}

/// The paths every OS crate builds from its roots, and the fake's.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AppPaths {
    data_dir: PathBuf,
    legacy: Vec<PathBuf>,
    local_dir: PathBuf,
    profile_key: String,
}

/// The data folder's name under the OS's roaming root.
pub const DATA_FOLDER: &str = "MightyCode";

impl AppPaths {
    /// `profile` when given (absolute), else `<roaming>/MightyCode` with the legacy folders `<roaming>/<legacy name>`; the local folder
    /// is `<local>/<identifier>`.
    pub fn resolve(
        profile: Option<PathBuf>,
        roaming: Option<PathBuf>,
        legacy_names: &[&str],
        local: Option<PathBuf>,
        identifier: &str,
    ) -> Result<AppPaths> {
        let local_dir = local.ok_or(PlatformError::Invalid("no local data folder is known".into()))?.join(identifier);
        match profile {
            Some(profile) => {
                if !profile.is_absolute() {
                    return Err(PlatformError::Invalid("--profile needs an absolute path".into()));
                }
                let profile_key = folder_name(&profile);
                Ok(AppPaths { data_dir: profile, legacy: vec![], local_dir, profile_key })
            }
            None => {
                let roaming = roaming.ok_or(PlatformError::Invalid("no data folder (%APPDATA%) is known".into()))?;
                let legacy = legacy_names.iter().map(|name| roaming.join(name)).collect();
                Ok(AppPaths { data_dir: roaming.join(DATA_FOLDER), legacy, local_dir, profile_key: "default".into() })
            }
        }
    }

    /// Everything under one folder (tests and the fake): data `<root>/data`, local `<root>/local`, no legacy folders.
    pub fn rooted(root: &Path) -> AppPaths {
        AppPaths { data_dir: root.join("data"), legacy: vec![], local_dir: root.join("local"), profile_key: "default".into() }
    }
}

impl Paths for AppPaths {
    fn data_dir(&self) -> &Path {
        &self.data_dir
    }
    fn legacy_data_dirs(&self) -> &[PathBuf] {
        &self.legacy
    }
    fn local_dir(&self) -> &Path {
        &self.local_dir
    }
    fn profile_key(&self) -> &str {
        &self.profile_key
    }
}

/// A profile path as one folder name: letters, digits and `-` kept, anything else `_`, the last 80 characters.
pub fn folder_name(path: &Path) -> String {
    let name: Vec<char> = path.to_string_lossy().chars().map(|c| if c.is_ascii_alphanumeric() || c == '-' { c } else { '_' }).collect();
    name[name.len().saturating_sub(80)..].iter().collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn root(name: &str) -> PathBuf {
        if cfg!(windows) { PathBuf::from(format!("C:\\{name}")) } else { PathBuf::from(format!("/{name}")) }
    }

    #[test]
    fn the_default_profile_lives_under_the_roaming_root() {
        let paths =
            AppPaths::resolve(None, Some(root("roam")), &["MightyClaude", "mighty-claude"], Some(root("local")), "dev.mightycode.tauri")
                .unwrap();
        assert_eq!(paths.data_dir(), root("roam").join("MightyCode"));
        assert_eq!(paths.legacy_data_dirs(), [root("roam").join("MightyClaude"), root("roam").join("mighty-claude")]);
        assert_eq!(paths.state_file(), root("roam").join("MightyCode").join("workspace-state.json"));
        assert_eq!(paths.state_backup(), root("roam").join("MightyCode").join("workspace-state.json.bak"));
        assert_eq!(paths.webview_dir(), root("local").join("dev.mightycode.tauri").join("webview").join("default"));
        assert_eq!(paths.browser_dir(), root("local").join("dev.mightycode.tauri").join("browser").join("default"));
    }

    #[test]
    fn a_profile_replaces_the_data_folder_and_reads_no_legacy_one() {
        let profile = root("work").join("profile one");
        let paths = AppPaths::resolve(Some(profile.clone()), Some(root("roam")), &["MightyClaude"], Some(root("local")), "id").unwrap();
        assert_eq!(paths.data_dir(), profile);
        assert!(paths.legacy_data_dirs().is_empty());
        assert_eq!(paths.profile_key(), folder_name(&profile));
        assert!(paths.webview_dir().ends_with(folder_name(&profile)));
        assert!(AppPaths::resolve(Some(PathBuf::from("relative")), None, &[], Some(root("local")), "id").is_err());
    }

    #[test]
    fn folder_names_keep_the_last_80_safe_characters() {
        assert_eq!(folder_name(Path::new("C:\\Users\\a b\\p-1")), "C__Users_a_b_p-1");
        let long = "x".repeat(100) + "end";
        assert_eq!(folder_name(Path::new(&long)).len(), 80);
        assert!(folder_name(Path::new(&long)).ends_with("end"));
    }
}
