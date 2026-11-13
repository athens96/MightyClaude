//! C# `CliDiscovery`'s checks in `OpencodeHermesSetupVerification` (MightyCode.Core.Tests/OpencodeHermesSetupVerification.cs):
//! the CliDiscovery parts only. The same checks' `SetupProber`, `SetupScript`, `SetupRuntimeSync` and ProviderCatalog-source parts
//! belong to the setup and run-engine stages.
#![cfg(windows)]

use mighty_platform_windows::Variables;
use mighty_platform_windows::discovery::{files, hermes_install_folder, version_text};

const HERMES_VERSION: &str = "Hermes Agent v0.23.1 (2026.9.24) · stable · local 3b4215785882";

fn hermes_output() -> String {
    format!(
        "{HERMES_VERSION}\nInstall directory: C:\\Users\\me\\AppData\\Local\\hermes\\hermes-agent\nInstall method: git\nPython: 3.14.9\nOpenAI SDK: 2.54.0\nUp to date\n"
    )
}

/// OpencodeHermesSetupVerification.cs:33-37.
fn windows_environment() -> Variables {
    [
        ("USERPROFILE", r"C:\Users\me"),
        ("LOCALAPPDATA", r"C:\Users\me\AppData\Local"),
        ("APPDATA", r"C:\Users\me\AppData\Roaming"),
        ("ProgramFiles", r"C:\Program Files"),
        ("SystemRoot", r"C:\Windows"),
    ]
    .into_iter()
    .collect()
}

fn with(name: &str, value: &str) -> Variables {
    let mut environment = windows_environment();
    environment.set(name, value);
    environment
}

/// C# "setup: Windows finds opencode and Hermes where their installers put them" (OpencodeHermesSetupVerification.cs:135-156), its
/// CliDiscovery part.
#[test]
fn windows_finds_opencode_and_hermes_where_their_installers_put_them() {
    let (npm, tools) = (r"C:\Users\me\AppData\Roaming\npm", r"C:\tools");
    let hermes_folder = r"C:\Users\me\AppData\Local\hermes\bin";
    let environment = windows_environment();
    assert_eq!(hermes_install_folder(&environment).as_deref(), Some(hermes_folder), "Hermes's installer folder");
    assert_eq!(
        hermes_install_folder(&with("hermes_home", r"D:\hermes")).as_deref(),
        Some(r"D:\hermes\bin"),
        "HERMES_HOME's bin, the variable named in any case"
    );
    assert_eq!(
        hermes_install_folder(&with("HERMES_HOME", "hermes")).as_deref(),
        Some(hermes_folder),
        "a relative HERMES_HOME is not the folder"
    );
    assert_eq!(hermes_install_folder(&Variables::new()), None, "no folder when the environment names none");
    // ProviderCatalog's discovery: folder by folder in the PATH's order, as a terminal finds the command.
    let opencode = files("opencode", [tools, npm], true, &environment);
    assert_eq!(
        opencode,
        [
            r"C:\tools\opencode.exe",
            r"C:\tools\node_modules\@opencode\cli\bin\opencode.exe",
            r"C:\tools\opencode.cmd",
            r"C:\Users\me\AppData\Roaming\npm\opencode.exe",
            r"C:\Users\me\AppData\Roaming\npm\node_modules\@opencode\cli\bin\opencode.exe",
            r"C:\Users\me\AppData\Roaming\npm\opencode.cmd",
        ],
        "opencode's binary, npm's copy of OpenCode 2's binary, then the shim"
    );
    let hermes = files("hermes", [tools], true, &environment);
    assert_eq!(
        hermes,
        [r"C:\tools\hermes.exe", r"C:\tools\hermes.cmd", &format!(r"{hermes_folder}\hermes.exe"), &format!(r"{hermes_folder}\hermes.cmd")],
        "hermes.exe, then its .cmd launcher, the installer's folder last"
    );
    assert_eq!(
        files("hermes", [hermes_folder.to_uppercase().as_str()], true, &environment).len(),
        2,
        "the installer's folder already on the PATH is tried once"
    );
    assert!(
        files("pi", [npm], true, &environment) == [format!(r"{npm}\pi.cmd")]
            && files("codex", [tools], true, &environment) == [r"C:\tools\codex.exe"]
            && files("gemini", [tools], true, &environment) == [r"C:\tools\agy.exe"]
            && files("claude", [tools], true, &environment) == [r"C:\tools\claude.exe"],
        "the other CLIs are found as before"
    );
    assert!(
        files("hermes", ["/usr/local/bin"], false, &environment) == [r"/usr/local/bin\hermes"]
            && files("opencode", ["/usr/local/bin"], false, &environment) == [r"/usr/local/bin\opencode"],
        "off Windows each folder's bare name, and no Windows folder (Path.Combine as Windows writes it)"
    );
}

/// C# "setup: only Hermes's first line is its version wherever Windows reads one" (OpencodeHermesSetupVerification.cs:194-200),
/// its CliDiscovery part.
#[test]
fn only_hermess_first_line_is_its_version_wherever_windows_reads_one() {
    assert_eq!(version_text("hermes", Some(&hermes_output())), HERMES_VERSION, "the first line");
    assert_eq!(
        version_text("hermes", Some(&format!("\r\n  {HERMES_VERSION}  \r\nInstall method: git\r\n"))),
        HERMES_VERSION,
        "Windows line ends and blank lines"
    );
    assert!(version_text("hermes", Some("")).is_empty() && version_text("hermes", None).is_empty(), "nothing printed is no version");
    assert!(
        version_text("claude", Some("2.1.300 (Claude Code)\n")) == "2.1.300 (Claude Code)"
            && version_text("codex", Some(" codex-cli 0.160.0 ")) == "codex-cli 0.160.0"
            && version_text("opencode", Some("opencode v2.2.7\r\n")) == "opencode v2.2.7",
        "any other CLI's output is trimmed, as before"
    );
}
