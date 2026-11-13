//! The tests' stand-in child: what C# `Verification.Self` relaunches the test exe as (MightyCode.Core.Tests/Program.cs:6-20,
//! Verification.cs:49-53). None of these is a real CLI. Std only, so it builds on every OS.
//!
//! - `--long-child <pid file>`: writes its id (to `<pid file>.ready`, then renamed), prints READY, sleeps 60 s.
//! - `--fail-child <text>`: writes the text to stderr and exits 3.
//! - `--noisy-child <bytes>`: writes lines of 255 `x` until that many bytes.
//! - `--group-child <pid file>`: starts `--long-child <pid file>` as a child of its own, prints READY, sleeps 60 s.
//! - `--fake-cli <provider> <record> [args…]`: records its arguments in `<record>.args`; `--version` prints 2.1.271.
//! - `--print-args <file> [args…]`: writes the arguments it received, NUL-separated, to the file (the `.cmd` argv checks).
//! - `--print-env <file>`: writes its environment, `NAME=value` entries NUL-separated, to the file.
//! - `--echo`: copies stdin to stdout.

use std::io::{Read, Write};
use std::process::{Command, Stdio};
use std::time::Duration;

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let words: Vec<&str> = args.iter().map(String::as_str).collect();
    match words.as_slice() {
        ["--long-child", pid_file] => long_child(pid_file),
        ["--fail-child", text] => {
            eprintln!("{text}");
            std::process::exit(3);
        }
        ["--noisy-child", count] => {
            let count: usize = count.parse().expect("a byte count");
            // Console.WriteLine's line end; the count steps by 256 as C#'s does.
            let line = format!("{}{}", "x".repeat(255), if cfg!(windows) { "\r\n" } else { "\n" });
            let mut out = std::io::stdout().lock();
            let mut written = 0;
            while written < count {
                if out.write_all(line.as_bytes()).is_err() {
                    break;
                }
                written += 256;
            }
        }
        ["--group-child", pid_file] => {
            let mut command = Command::new(std::env::current_exe().expect("own path"));
            command.args(["--long-child", pid_file]).stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::piped());
            #[cfg(windows)]
            {
                use std::os::windows::process::CommandExt;
                // CREATE_NO_WINDOW, as C#'s ProcessStartInfo.CreateNoWindow.
                command.creation_flags(0x0800_0000);
            }
            // Never waited for: the grandchild must outlive this process's own end, until its job kills it.
            #[allow(clippy::zombie_processes)]
            let _grandchild = command.spawn().expect("fixture grandchild did not start");
            println!("READY");
            std::thread::sleep(Duration::from_secs(60));
        }
        ["--fake-cli", _provider, record, rest @ ..] => {
            std::fs::write(format!("{record}.args"), rest.join("\0")).expect("record");
            if rest.contains(&"--version") {
                println!("2.1.271");
            }
        }
        ["--print-args", file, rest @ ..] => std::fs::write(file, rest.join("\0")).expect("args file"),
        ["--print-env", file] => {
            let entries: Vec<String> =
                std::env::vars_os().map(|(name, value)| format!("{}={}", name.to_string_lossy(), value.to_string_lossy())).collect();
            std::fs::write(file, entries.join("\0")).expect("env file");
        }
        ["--echo"] => {
            let mut input = Vec::new();
            std::io::stdin().read_to_end(&mut input).expect("stdin");
            std::io::stdout().write_all(&input).expect("stdout");
        }
        _ => {
            eprintln!("mighty-fake-child: unknown mode {args:?}");
            std::process::exit(64);
        }
    }
}

fn long_child(pid_file: &str) {
    let ready = format!("{pid_file}.ready");
    std::fs::write(&ready, std::process::id().to_string()).expect("pid file");
    std::fs::rename(&ready, pid_file).expect("pid file rename");
    println!("READY");
    std::thread::sleep(Duration::from_secs(60));
}
