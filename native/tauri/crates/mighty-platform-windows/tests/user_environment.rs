//! C# `UserEnvironmentVerification` (MightyCode.Core.Tests/UserEnvironmentVerification.cs).
#![cfg(windows)]

use std::collections::BTreeMap;

use mighty_platform_windows::user_environment::{fresh, is_bare, missing, parse};

/// C# "an app started from a bare environment takes the user's own variables back" (UserEnvironmentVerification.cs:7-29).
#[test]
fn repair() {
    // The helper's environment is SystemRoot alone: bare.
    let helper: BTreeMap<String, String> = BTreeMap::from([("SystemRoot".into(), r"C:\Windows".into())]);
    let read = |env: &BTreeMap<String, String>, name: &str| {
        env.iter().find(|(key, _)| key.eq_ignore_ascii_case(name)).map(|(_, value)| value.clone())
    };
    assert!(is_bare(|name| read(&helper, name)), "an environment with only SystemRoot is bare");
    assert!(
        !is_bare(|name| match name {
            "USERPROFILE" => Some(r"C:\Users\u".into()),
            "APPDATA" => Some("a".into()),
            "LOCALAPPDATA" => Some("l".into()),
            _ => None,
        }),
        "a whole environment is not bare"
    );
    assert!(is_bare(|name| Some(if name == "USERPROFILE" { String::new() } else { "x".into() })), "an empty USERPROFILE is bare");

    let fresh_block = parse(
        "=C:=C:\\\0USERPROFILE=C:\\Users\\u\0APPDATA=C:\\Users\\u\\AppData\\Roaming\0PATHEXT=.COM;.EXE;.BAT;.CMD\0SystemRoot=D:\\Other\0Path=C:\\Windows\\System32\0EMPTY=\0\0",
    );
    assert!(!fresh_block.contains("") && !fresh_block.names().any(|name| name.starts_with('=')), "drive-letter entries are left out");
    assert!(
        fresh_block.get("userprofile") == Some(r"C:\Users\u") && fresh_block.get("PATH") == Some(r"C:\Windows\System32"),
        "the parsed block is case-blind"
    );

    let missing: BTreeMap<String, String> = missing(|name| read(&helper, name), &fresh_block).into_iter().collect();
    let has = |name: &str| missing.keys().any(|key| key.eq_ignore_ascii_case(name));
    assert!(
        has("USERPROFILE")
            && has("APPDATA")
            && missing.get("PATHEXT").map(String::as_str) == Some(".COM;.EXE;.BAT;.CMD")
            && missing.contains_key("Path"),
        "the missing user variables are added"
    );
    assert!(!has("SystemRoot"), "a variable the process already has keeps its value");
    assert!(!has("EMPTY"), "an empty fresh value adds nothing");

    // On Windows the real block names the user's home, so a relaunched app has one.
    let real = fresh().expect("Windows builds the user's environment");
    assert!(
        real.get("USERPROFILE").is_some_and(|home| !home.is_empty()) && real.contains("PATHEXT"),
        "the user's fresh environment has USERPROFILE and PATHEXT"
    );
}
