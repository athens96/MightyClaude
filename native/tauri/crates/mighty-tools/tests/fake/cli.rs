//! The checks' stand-in CLI for setup, the toolkit and the status line (C#'s `Verification.Self` relaunching the test exe), run
//! through Windows' own `ProcessHost`. None of these is a real tool. std only.
//!
//! - `--version-print <text>`: what a CLI's `--version` prints, then exit 0 (the setup probe's version read).
//! - `--status-echo`: reads its stdin to the end and prints its first line between the ANSI cyan and reset codes (the status line's
//!   payload fed and its colour kept).
//! - `--sleep <seconds>`: waits, then exits 0 (a command past its deadline, or one cancelled).
//! - `--noisy-stderr <count>`: writes `count` lines to stderr, then `done` to stdout (stderr drained past the pipe's capacity).
//! - `--exit <code>` [text]: prints `text` to stderr and exits with `code` (a failing toolkit step).

use std::io::{Read, Write};

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let arg = |index: usize| args.get(index).map(String::as_str).unwrap_or("");
    match arg(0) {
        "--version-print" => {
            println!("{}", arg(1));
        }
        "--status-echo" => {
            let mut input = String::new();
            let _ = std::io::stdin().read_to_string(&mut input);
            let first = input.lines().next().unwrap_or("");
            let mut out = std::io::stdout().lock();
            let _ = write!(out, "\u{1b}[36m{first}\u{1b}[0m");
            let _ = out.flush();
        }
        "--sleep" => {
            let seconds: u64 = arg(1).parse().unwrap_or(60);
            std::thread::sleep(std::time::Duration::from_secs(seconds));
        }
        "--noisy-stderr" => {
            let count: usize = arg(1).parse().unwrap_or(12_000);
            let mut err = std::io::stderr().lock();
            for _ in 0..count {
                let _ = writeln!(err, "discarded-stderr");
            }
            drop(err);
            print!("done");
        }
        "--exit" => {
            eprint!("{}", arg(2));
            std::process::exit(arg(1).parse().unwrap_or(1));
        }
        _ => {}
    }
}
