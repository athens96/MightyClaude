//! The vault's files and .NET's: a key the Rust vault saves, .NET's `ProtectedData.Unprotect` (what C# `DpapiRemoteKeyVault`
//! calls) reads with the C# entropy, and a blob .NET protected loads in the Rust vault. Windows PowerShell 5.1 stands in for the
//! C# app (it ships with Windows and carries System.Security).
#![cfg(windows)]

use std::path::PathBuf;
use std::process::Command;
use std::time::{SystemTime, UNIX_EPOCH};

use mighty_platform::SecretVault;
use mighty_platform_windows::{DpapiVault, REMOTE_KEYS_FOLDER, remote_key_file_name};

#[allow(clippy::disallowed_methods, reason = "a check's own tool, standing in for the C# app; this test starts no child of the app")]
fn powershell(script: &str, env: &[(&str, &str)]) -> String {
    let mut command = Command::new("powershell.exe");
    command.args(["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script]);
    for (name, value) in env {
        command.env(name, value);
    }
    let output = command.output().expect("powershell.exe");
    assert!(output.status.success(), "{}", String::from_utf8_lossy(&output.stderr));
    String::from_utf8(output.stdout).unwrap().trim().to_string()
}

const ENTROPY: &str = "$entropy = [Text.Encoding]::UTF8.GetBytes(\"MightyCode remote key v2`0\" + $env:ID);";

#[test]
fn dotnet_reads_what_the_vault_writes_and_the_other_way_round() {
    let nanos = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
    let directory: PathBuf = std::env::temp_dir().join(format!("mighty-dpapi-interop-{}-{nanos}", std::process::id()));
    let vault = DpapiVault::new(directory.clone());
    let folder = directory.join(REMOTE_KEYS_FOLDER);

    vault.save("conn-rust", "written by rust: 한글").unwrap();
    let path = folder.join(remote_key_file_name("conn-rust"));
    let read = powershell(
        &format!(
            "Add-Type -AssemblyName System.Security; {ENTROPY} $plain = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($env:BLOB), $entropy, 'CurrentUser'); [Convert]::ToBase64String($plain)"
        ),
        &[("ID", "conn-rust"), ("BLOB", path.to_str().unwrap())],
    );
    assert_eq!(read, "d3JpdHRlbiBieSBydXN0OiDtlZzquIA=", "base64 of the UTF-8 text");

    let path = folder.join(remote_key_file_name("conn-dotnet"));
    powershell(
        &format!(
            "Add-Type -AssemblyName System.Security; {ENTROPY} $blob = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes('written by dotnet'), $entropy, 'CurrentUser'); [IO.File]::WriteAllBytes($env:BLOB, $blob)"
        ),
        &[("ID", "conn-dotnet"), ("BLOB", path.to_str().unwrap())],
    );
    assert_eq!(vault.load("conn-dotnet").as_deref(), Some("written by dotnet"));
    let _ = std::fs::remove_dir_all(directory);
}
