//! The command line (C# `StartupOptions.Parse`, MightyCode.WinUI/Program.cs), with the skeleton's own switches.
//!
//! `--profile <absolute folder>`  the state folder instead of `%APPDATA%\MightyCode` (never read from the legacy folders)
//! `--smoke-test`                 run the smoke (smoke.rs); needs `--profile`
//! `--smoke-exit`                 quit when the smoke has its result, exit code 0 when it passed, else 1
//! `--smoke-out <file>`           where the smoke writes its result (default `<profile>\smoke-result.json`)
//! `--log-dir <folder>`           the host's logs (default `<profile>\tauri-logs`)
//! `--remote-debugging-port <n>`  opens WebView2's DevTools protocol on that port (screenshots, inspection)
//! `--allow-state-write`          lets the host write the profile's state (saves, the damaged-file recovery, the legacy copy) as
//!                                C# does; without it nothing in the profile is written but the logs and the smoke's result
//! `--smoke-fake-cli <exe>`       the smoke's Claude is mighty-run's stand-in CLI (`mighty-fake-cli --replay-cli`), every other
//!                                provider missing; the smoke then runs its composer, cards and Mighty steps against it
//! `--smoke-real-claude`          the smoke sends one real request with the installed `claude` CLI and waits for its answer
//! `--native-dump <file>`         every `native` and `text` push written to the file as sent, one per line (native.rs; the parity
//!                                capture's Rust side, plan W6 §1.8)

use std::path::PathBuf;

#[derive(Debug, Default, Clone)]
pub struct Options {
    pub profile: Option<PathBuf>,
    pub smoke_test: bool,
    pub smoke_exit: bool,
    pub smoke_out: Option<PathBuf>,
    pub log_dir: Option<PathBuf>,
    pub remote_debugging_port: Option<u16>,
    pub allow_state_write: bool,
    pub smoke_fake_cli: Option<PathBuf>,
    pub smoke_real_claude: bool,
    pub native_dump: Option<PathBuf>,
}

fn absolute(flag: &str, value: Option<String>) -> Result<PathBuf, String> {
    let path = PathBuf::from(value.ok_or_else(|| format!("{flag} needs a path."))?);
    if !path.is_absolute() {
        return Err(format!("{flag} needs an absolute path."));
    }
    Ok(path)
}

pub fn parse(args: impl IntoIterator<Item = String>) -> Result<Options, String> {
    let mut options = Options::default();
    let mut args = args.into_iter();
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--profile" if options.profile.is_none() => options.profile = Some(absolute("--profile", args.next())?),
            "--smoke-test" => options.smoke_test = true,
            "--smoke-exit" => options.smoke_exit = true,
            "--allow-state-write" => options.allow_state_write = true,
            "--smoke-fake-cli" => options.smoke_fake_cli = Some(absolute("--smoke-fake-cli", args.next())?),
            "--smoke-real-claude" => options.smoke_real_claude = true,
            "--smoke-out" => options.smoke_out = Some(absolute("--smoke-out", args.next())?),
            "--log-dir" => options.log_dir = Some(absolute("--log-dir", args.next())?),
            "--native-dump" => options.native_dump = Some(absolute("--native-dump", args.next())?),
            "--remote-debugging-port" => {
                let port = args.next().and_then(|p| p.parse().ok()).ok_or("--remote-debugging-port needs a port number.")?;
                options.remote_debugging_port = Some(port);
            }
            other => return Err(format!("Unsupported argument: {other}")),
        }
    }
    if options.smoke_exit && !options.smoke_test {
        return Err("--smoke-exit needs --smoke-test.".into());
    }
    if (options.smoke_fake_cli.is_some() || options.smoke_real_claude) && !options.smoke_test {
        return Err("--smoke-fake-cli and --smoke-real-claude need --smoke-test.".into());
    }
    if options.smoke_fake_cli.is_some() && options.smoke_real_claude {
        return Err("--smoke-fake-cli and --smoke-real-claude go one at a time.".into());
    }
    if options.smoke_test && options.profile.is_none() {
        return Err("--smoke-test needs --profile.".into());
    }
    if options.profile.as_ref().is_some_and(|p| p.is_file()) {
        return Err("--profile names a file, not a folder.".into());
    }
    Ok(options)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parsed(args: &[&str]) -> Result<Options, String> {
        parse(args.iter().map(|a| a.to_string()))
    }

    #[test]
    fn smoke_switches_go_together() {
        let root = if cfg!(windows) { "C:\\p" } else { "/p" };
        assert!(parsed(&["--smoke-test", "--smoke-exit", "--profile", root]).is_ok());
        assert!(parsed(&["--smoke-exit", "--profile", root]).is_err());
        assert!(parsed(&["--smoke-test"]).is_err());
        assert!(parsed(&["--profile", "relative"]).is_err());
        assert!(parsed(&["--bogus"]).is_err());
        assert_eq!(parsed(&["--remote-debugging-port", "9222"]).unwrap().remote_debugging_port, Some(9222));
        assert!(!parsed(&[]).unwrap().allow_state_write, "writing is off unless asked for");
        assert!(parsed(&["--allow-state-write"]).unwrap().allow_state_write);
        let fake = if cfg!(windows) { "C:\\f.exe" } else { "/f" };
        assert!(parsed(&["--smoke-fake-cli", fake]).is_err(), "the stand-in CLI is the smoke's");
        assert!(parsed(&["--smoke-test", "--profile", root, "--smoke-fake-cli", fake]).unwrap().smoke_fake_cli.is_some());
        assert!(parsed(&["--smoke-test", "--profile", root, "--smoke-fake-cli", fake, "--smoke-real-claude"]).is_err());
        assert!(parsed(&["--smoke-test", "--profile", root, "--smoke-real-claude"]).unwrap().smoke_real_claude);
        let dump = if cfg!(windows) { "C:\\d\\native.jsonl" } else { "/d/native.jsonl" };
        assert!(parsed(&["--native-dump", dump]).unwrap().native_dump.is_some());
        assert!(parsed(&["--native-dump", "native.jsonl"]).is_err(), "the dump's path is absolute");
    }
}
