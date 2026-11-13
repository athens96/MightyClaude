//! What the ported C# checks share: the shared fixtures, the language the C# runner pins (ko), temporary folders and links.
#![allow(dead_code)]

use std::path::{Path, PathBuf};

use mighty_core::locale::Locale;

/// The C# runner pins the language to Korean (Verification.cs).
pub fn ko() -> Locale {
    Locale::new("ko")
}

/// `native/contracts/fixtures/<name>`, parsed.
pub fn fixture(name: &str) -> serde_json::Value {
    let path = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../contracts/fixtures").join(name);
    let text = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    serde_json::from_str(&text).unwrap_or_else(|e| panic!("{}: {e}", path.display()))
}

/// A folder of its own under the temporary folder, removed when dropped (C# `Verification.Temp`).
pub struct Temp(pub PathBuf);

impl Temp {
    pub fn new(name: &str) -> Temp {
        let path = std::env::temp_dir().join(format!("mighty-files-{name}-{}", mighty_core::wire::new_id()));
        std::fs::create_dir_all(&path).unwrap();
        Temp(path)
    }
    pub fn path(&self) -> &Path {
        &self.0
    }
    pub fn text(&self) -> String {
        self.0.to_string_lossy().into_owned()
    }
}

impl Drop for Temp {
    fn drop(&mut self) {
        // git marks its objects read-only on Windows.
        fn writable(path: &Path) {
            for item in std::fs::read_dir(path).into_iter().flatten().flatten() {
                let path = item.path();
                if path.is_dir() && !path.is_symlink() {
                    writable(&path);
                } else if let Ok(meta) = std::fs::metadata(&path) {
                    let mut permissions = meta.permissions();
                    #[allow(clippy::permissions_set_readonly_false, reason = "the test's own files, removed next")]
                    permissions.set_readonly(false);
                    let _ = std::fs::set_permissions(&path, permissions);
                }
            }
        }
        writable(&self.0);
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

pub fn write(root: &Path, relative: &str, bytes: &[u8]) -> PathBuf {
    let path = root.join(relative.replace('/', std::path::MAIN_SEPARATOR_STR));
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(&path, bytes).unwrap();
    path
}

/// A link; false where the OS refuses (a Windows account without the symlink right).
pub fn link(at: &Path, target: &Path, directory: bool) -> bool {
    #[cfg(windows)]
    {
        if directory {
            std::os::windows::fs::symlink_dir(target, at).is_ok()
        } else {
            std::os::windows::fs::symlink_file(target, at).is_ok()
        }
    }
    #[cfg(unix)]
    {
        let _ = directory;
        std::os::unix::fs::symlink(target, at).is_ok()
    }
}

/// The files ports of the OS the checks run on: the handle is asked on Windows, the path walked again elsewhere.
pub fn handles() -> std::sync::Arc<dyn mighty_platform::FileHandles> {
    #[cfg(windows)]
    {
        std::sync::Arc::new(mighty_platform_windows::WindowsFileHandles)
    }
    #[cfg(not(windows))]
    {
        std::sync::Arc::new(mighty_platform::unsupported::Unsupported)
    }
}
