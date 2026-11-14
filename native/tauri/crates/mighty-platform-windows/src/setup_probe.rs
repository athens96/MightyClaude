//! Where Windows has a command, as the setup screen finds a CLI (C# `SetupProber.Executables`, SetupProbe.cs:78, and
//! `SetupScript.WellKnownFolders`, SetupPlan.cs:95): the PATH (64 folders), then the folders the installers put their programs in,
//! each copy once with the first PATHEXT extension found. The composer's "send to" switch reads which target CLIs this PC has with it.

use std::path::Path;

use crate::Variables;
use crate::command::{copies, file_exists};
use crate::discovery::hermes_install_folder;
use crate::win::combine;

/// `SetupScript.WellKnownFolders`: the installers' folders under the environment's roots (a root that is not a whole path is left
/// out; a drive path `C:\…` counts as whole on any system).
pub fn well_known_folders(variables: &Variables) -> Vec<String> {
    let under = |variable: &str, parts: &[&str]| -> Option<String> {
        let root = variables.get(variable).filter(|r| !r.is_empty())?;
        let bytes = root.as_bytes();
        let drive = bytes.len() >= 3 && bytes[1] == b':' && bytes[2] == b'\\';
        (Path::new(root).is_absolute() || drive).then(|| parts.iter().fold(root.to_string(), |folder, part| combine(&folder, part)))
    };
    [
        under("USERPROFILE", &[".local", "bin"]),
        under("LOCALAPPDATA", &["Programs", "OpenAI", "Codex", "bin"]),
        under("LOCALAPPDATA", &["agy", "bin"]),
        hermes_install_folder(variables),
        under("APPDATA", &["npm"]),
        under("LOCALAPPDATA", &["Microsoft", "WinGet", "Links"]),
        under("LOCALAPPDATA", &["Programs", "Git", "cmd"]),
        under("ProgramFiles", &["Git", "cmd"]),
        under("ProgramFiles", &["nodejs"]),
        under("ProgramFiles", &["GitHub CLI"]),
        under("ProgramFiles", &["Tailscale"]),
    ]
    .into_iter()
    .flatten()
    .collect()
}

/// `SetupProber.Executables(name, environment)`: each copy of `name` along the PATH and then in the installers' folders, in order,
/// once.
pub fn executables(name: &str, environment: &Variables) -> Vec<String> {
    let path = environment.get("PATH").unwrap_or("");
    let mut folders: Vec<String> = path.split(';').take(64).map(str::to_string).collect();
    folders.extend(well_known_folders(environment));
    copies(name, folders.iter().map(String::as_str), environment.get("PATHEXT"), &file_exists)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_installers_folders_come_after_the_path_in_setups_order() {
        let variables: Variables = [
            ("USERPROFILE", r"C:\Users\u"),
            ("LOCALAPPDATA", r"C:\Users\u\AppData\Local"),
            ("APPDATA", r"C:\Users\u\AppData\Roaming"),
            ("ProgramFiles", r"C:\Program Files"),
            ("HERMES_HOME", "relative"),
        ]
        .into_iter()
        .collect();
        assert_eq!(
            well_known_folders(&variables),
            [
                r"C:\Users\u\.local\bin",
                r"C:\Users\u\AppData\Local\Programs\OpenAI\Codex\bin",
                r"C:\Users\u\AppData\Local\agy\bin",
                r"C:\Users\u\AppData\Local\hermes\bin",
                r"C:\Users\u\AppData\Roaming\npm",
                r"C:\Users\u\AppData\Local\Microsoft\WinGet\Links",
                r"C:\Users\u\AppData\Local\Programs\Git\cmd",
                r"C:\Program Files\Git\cmd",
                r"C:\Program Files\nodejs",
                r"C:\Program Files\GitHub CLI",
                r"C:\Program Files\Tailscale",
            ]
        );
        let relative: Variables = [("USERPROFILE", "home")].into_iter().collect();
        assert!(well_known_folders(&relative).is_empty(), "a root that is not a whole path is left out");
    }

    #[test]
    fn a_command_is_found_along_the_path_with_pathext() {
        let folder = std::env::temp_dir().join(format!("mighty-setup-probe-{}", std::process::id()));
        std::fs::create_dir_all(&folder).unwrap();
        std::fs::write(folder.join("codex.cmd"), "").unwrap();
        let text = folder.to_string_lossy().into_owned();
        let variables: Variables = [("PATH", format!("{text};{text}")), ("PATHEXT", ".EXE;.CMD".to_string())].into_iter().collect();
        assert_eq!(executables("codex", &variables), [combine(&text, "codex.CMD")], "once, with the extension PATHEXT names");
        assert!(executables("agy", &variables).is_empty());
        let _ = std::fs::remove_dir_all(&folder);
    }
}
