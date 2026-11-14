//! What the setup script and probes read of a Windows environment, written as Windows writes it whatever OS builds this crate (the
//! script only ever runs on Windows): names in any case (C# `WindowsCommand.Variables`), `Path.Combine` with `\`, a whole path as
//! `Path.IsPathFullyQualified` reads one on Windows, Hermes's installer folder (C# `CliDiscovery.HermesInstallFolder`) and the
//! installers' folders (C# `SetupScript.WellKnownFolders`).

use std::collections::BTreeMap;

/// `variables[name]` with the name in any case (Windows' rule): the last spelling the map holds wins.
pub fn variable<'a>(environment: &'a BTreeMap<String, String>, name: &str) -> Option<&'a str> {
    environment.iter().filter(|(key, _)| key.eq_ignore_ascii_case(name)).map(|(_, value)| value.as_str()).next_back()
}

/// `map[name] = value` ignoring case: an entry already there in another spelling takes the value.
pub fn set_variable(environment: &mut BTreeMap<String, String>, name: &str, value: &str) {
    let key = environment.keys().find(|key| key.eq_ignore_ascii_case(name)).cloned().unwrap_or_else(|| name.to_string());
    environment.insert(key, value.to_string());
}

/// `Path.Combine(folder, name)` as .NET writes it on Windows: `name` when it is rooted, else the two joined by `\` unless the
/// folder already ends in a separator or a drive's colon.
pub fn combine(folder: &str, name: &str) -> String {
    if name.is_empty() {
        return folder.to_string();
    }
    let drive = name.as_bytes().first().is_some_and(u8::is_ascii_alphabetic) && name.as_bytes().get(1) == Some(&b':');
    if folder.is_empty() || name.starts_with(['\\', '/']) || drive {
        return name.to_string();
    }
    if folder.ends_with(['\\', '/', ':']) { format!("{folder}{name}") } else { format!("{folder}\\{name}") }
}

/// `Path.Combine` of several parts.
pub fn combine_all(root: &str, parts: &[&str]) -> String {
    parts.iter().fold(root.to_string(), |folder, part| combine(&folder, part))
}

/// `Path.IsPathFullyQualified` on Windows: a drive and a separator (`C:\`, `C:/`), or a UNC or device path (`\\…`, `//…`).
pub fn fully_qualified(path: &str) -> bool {
    let bytes = path.as_bytes();
    let separator = |b: u8| b == b'\\' || b == b'/';
    if bytes.len() >= 2 && separator(bytes[0]) && separator(bytes[1]) {
        return true;
    }
    bytes.len() >= 3 && bytes[0].is_ascii_alphabetic() && bytes[1] == b':' && separator(bytes[2])
}

/// A whole path (`C:\…` counts on any system, CliDiscovery.cs:24), never empty or holding NUL.
fn whole(path: Option<&str>) -> Option<&str> {
    let path = path.filter(|path| !path.is_empty() && !path.contains('\0'))?;
    let bytes = path.as_bytes();
    let drive = bytes.len() >= 3 && bytes[1] == b':' && bytes[2] == b'\\';
    (fully_qualified(path) || drive).then_some(path)
}

/// C# `CliDiscovery.HermesInstallFolder`: `$HERMES_HOME\bin` when HERMES_HOME is a whole path, else `%LOCALAPPDATA%\hermes\bin`;
/// None when the environment names neither.
pub fn hermes_install_folder(environment: &BTreeMap<String, String>) -> Option<String> {
    if let Some(home) = whole(variable(environment, "HERMES_HOME")) {
        return Some(combine(home, "bin"));
    }
    whole(variable(environment, "LOCALAPPDATA")).map(|local| combine_all(local, &["hermes", "bin"]))
}

/// C# `SetupScript.WellKnownFolders` (SetupPlan.cs:95): where the official installers put what they install, which a running
/// process's PATH may not list yet — Claude Code's and uv's `%USERPROFILE%\.local\bin`, Codex's, Antigravity's, Hermes's, npm's,
/// winget's links, Git for Windows (user and machine), Node.js, GitHub CLI and Tailscale. A root that is not a whole path is left out.
pub fn well_known_folders(environment: &BTreeMap<String, String>) -> Vec<String> {
    let under = |name: &str, parts: &[&str]| -> Option<String> {
        let root = variable(environment, name).filter(|root| !root.is_empty())?;
        let bytes = root.as_bytes();
        let drive = bytes.len() >= 3 && bytes[1] == b':' && bytes[2] == b'\\';
        (fully_qualified(root) || drive).then(|| combine_all(root, parts))
    };
    [
        under("USERPROFILE", &[".local", "bin"]),
        under("LOCALAPPDATA", &["Programs", "OpenAI", "Codex", "bin"]),
        under("LOCALAPPDATA", &["agy", "bin"]),
        hermes_install_folder(environment),
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

/// .NET's `int.TryParse(text)` (NumberStyles.Integer, invariant digits): white space around, one sign, ASCII digits, an Int32.
pub fn parse_int(text: &str) -> Option<i32> {
    let trimmed = text.trim_matches(|c: char| matches!(c, '\t' | '\n' | '\u{b}' | '\u{c}' | '\r' | ' '));
    let (negative, digits) = match trimmed.as_bytes().first() {
        Some(b'-') => (true, &trimmed[1..]),
        Some(b'+') => (false, &trimmed[1..]),
        _ => (false, trimmed),
    };
    if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let mut value: i64 = 0;
    for b in digits.bytes() {
        value = value * 10 + (b - b'0') as i64;
        if value > i32::MAX as i64 + 1 {
            return None;
        }
    }
    let value = if negative { -value } else { value };
    i32::try_from(value).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn environment(pairs: &[(&str, &str)]) -> BTreeMap<String, String> {
        pairs.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect()
    }

    #[test]
    fn the_installers_folders_follow_setups_order() {
        let env = environment(&[
            ("USERPROFILE", r"C:\Users\u"),
            ("LOCALAPPDATA", r"C:\Users\u\AppData\Local"),
            ("APPDATA", r"C:\Users\u\AppData\Roaming"),
            ("ProgramFiles", r"C:\Program Files"),
            ("HERMES_HOME", "relative"),
        ]);
        assert_eq!(
            well_known_folders(&env),
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
        assert!(well_known_folders(&environment(&[("USERPROFILE", "home")])).is_empty());
        assert_eq!(variable(&environment(&[("Path", "p")]), "PATH"), Some("p"));
    }

    #[test]
    fn integers_read_as_dotnet_reads_them() {
        assert_eq!(parse_int(" 42 "), Some(42));
        assert_eq!(parse_int("-7"), Some(-7));
        assert_eq!(parse_int("+3"), Some(3));
        assert_eq!(parse_int("2147483648"), None);
        assert_eq!(parse_int("-2147483648"), Some(i32::MIN));
        assert_eq!(parse_int(""), None);
        assert_eq!(parse_int("1.0"), None);
    }
}
