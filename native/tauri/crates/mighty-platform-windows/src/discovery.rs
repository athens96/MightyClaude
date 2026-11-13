//! Where Windows finds an agent CLI whose installer leaves more than one `<name>.exe` along the PATH, and what of its `--version`
//! is its version (C# `CliDiscovery`, CliDiscovery.cs): ProviderCatalog's discovery, the setup probe and the CLI update's reading
//! agree on both, so a setup row and the runtime show the same version.

use std::collections::BTreeSet;
use std::path::Path;

use crate::Variables;
use crate::win::{combine, fold};

/// The program a provider's CLI is (C# `ProviderCatalog.Executable`, ProviderCatalog.cs:36): Antigravity's is `agy`.
pub fn executable(provider: &str) -> &str {
    if provider == "gemini" { "agy" } else { provider }
}

/// A drive path (`C:\…`) counts as whole on any system, as `SetupScript.WellKnownFolders` reads it (CliDiscovery.cs:24).
fn whole(path: Option<&str>) -> Option<&str> {
    let path = path.filter(|path| !path.is_empty() && !path.contains('\0'))?;
    let bytes = path.as_bytes();
    let drive = bytes.len() >= 3 && bytes[1] == b':' && bytes[2] == b'\\';
    (Path::new(path).is_absolute() || drive).then_some(path)
}

/// Where Hermes Agent's own installer (install.ps1) puts `hermes.exe`, or the `hermes.cmd` it writes when it cannot make the
/// .exe: `$HERMES_HOME\bin` when HERMES_HOME is a whole path, else `%LOCALAPPDATA%\hermes\bin` (CliDiscovery.cs:16-21). None when
/// the environment names neither folder. Names are read in any case.
pub fn hermes_install_folder(environment: &Variables) -> Option<String> {
    if let Some(home) = whole(environment.get("HERMES_HOME")) {
        return Some(combine(home, "bin"));
    }
    whole(environment.get("LOCALAPPDATA")).map(|local| combine(&combine(local, "hermes"), "bin"))
}

/// The files tried for `provider`, in order, along `directories` (the PATH, then the fixed folders) (CliDiscovery.cs:34-47). Off
/// Windows each folder's bare name. On Windows, folder by folder as the PATH orders them, as a terminal would find the command:
/// opencode's `opencode.exe`, then npm's copy of OpenCode 2's own binary (`node_modules\@opencode\cli\bin\opencode.exe`, which the
/// npm shim only forwards to), then the shim `opencode.cmd` itself; Hermes's `hermes.exe`, then `hermes.cmd`, its installer's folder
/// last ([`hermes_install_folder`]); pi's npm shim `pi.cmd`; any other CLI its `<name>.exe`. Each file once (ignoring case on Windows).
pub fn files<'a>(provider: &str, directories: impl IntoIterator<Item = &'a str>, windows: bool, environment: &Variables) -> Vec<String> {
    let name = executable(provider);
    let mut folders: Vec<String> =
        directories.into_iter().filter(|folder| !folder.is_empty() && !folder.contains('\0')).map(String::from).collect();
    if windows
        && provider == "hermes"
        && let Some(hermes) = hermes_install_folder(environment)
    {
        folders.push(hermes);
    }
    let names = |folder: &str| -> Vec<String> {
        if !windows {
            return vec![combine(folder, name)];
        }
        match provider {
            "opencode" => vec![
                combine(folder, "opencode.exe"),
                combine(&combine(&combine(&combine(&combine(folder, "node_modules"), "@opencode"), "cli"), "bin"), "opencode.exe"),
                combine(folder, "opencode.cmd"),
            ],
            "hermes" => vec![combine(folder, "hermes.exe"), combine(folder, "hermes.cmd")],
            "pi" => vec![combine(folder, "pi.cmd")],
            _ => vec![combine(folder, &format!("{name}.exe"))],
        }
    };
    let mut seen = BTreeSet::new();
    folders.iter().flat_map(|folder| names(folder)).filter(|file| seen.insert(if windows { fold(file) } else { file.clone() })).collect()
}

/// Hermes's version line (C# `HermesAgent.VersionLine`, AcpAgents.cs:88): the first line that is not blank, trimmed.
fn hermes_version_line(output: &str) -> String {
    output.split('\n').map(str::trim).find(|line| !line.is_empty()).unwrap_or_default().to_string()
}

/// The version a CLI's `--version` printed, trimmed (CliDiscovery.cs:54-58): `hermes --version` prints its version line first, then
/// its install folder, Python and update status, so for Hermes only that first line is the version. `program` is the program's
/// name (`hermes`).
pub fn version_text(program: &str, output: Option<&str>) -> String {
    let text = output.unwrap_or("").trim();
    if program == "hermes" { hermes_version_line(text) } else { text.to_string() }
}
