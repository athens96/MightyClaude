//! The command line (C# `StartupOptions.Parse`, MightyCode.WinUI/Program.cs), with the skeleton's own switches.
//!
//! `--profile <absolute folder>`  the state folder instead of `%APPDATA%\MightyCode` (never read from the legacy folders)
//! `--smoke-test`                 run the smoke (smoke.rs); needs `--profile`
//! `--smoke-exit`                 quit when the smoke has its result, exit code 0 when it passed, else 1
//! `--smoke-out <file>`           where the smoke writes its result (default `<profile>\smoke-result.json`)
//! `--log-dir <folder>`           the host's logs (default `<profile>\tauri-logs`)
//! `--remote-debugging-port <n>`  opens WebView2's DevTools protocol on that port (screenshots, inspection)

use std::path::PathBuf;

#[derive(Debug, Default, Clone)]
pub struct Options {
    pub profile: Option<PathBuf>,
    pub smoke_test: bool,
    pub smoke_exit: bool,
    pub smoke_out: Option<PathBuf>,
    pub log_dir: Option<PathBuf>,
    pub remote_debugging_port: Option<u16>,
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
            "--smoke-out" => options.smoke_out = Some(absolute("--smoke-out", args.next())?),
            "--log-dir" => options.log_dir = Some(absolute("--log-dir", args.next())?),
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
    }
}
