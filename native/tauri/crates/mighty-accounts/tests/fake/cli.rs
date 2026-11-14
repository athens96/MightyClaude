//! The checks' stand-in CLI for the accounts and their usage, relaunched by the tests (as C# relaunches its test exe: `Verification.Self`).
//! None of these is a real CLI; std and serde_json only.
//!
//! - `--agy-usage-cli <state> [args…]` (C# AntigravityAccountUsageVerification.FakeAgyUsageAsync): agy answering `-p /usage
//!   --output-format json` as 1.5.5 does. Records its argv in `<state>/agy-argv.json` and how it ran in `<state>/agy-run.json`
//!   (`NO_COLOR`, `CI`, and whether the marker only the temporary folder holds is in its working folder); with
//!   `<state>/agy-signed-out` present it says it is signed out on standard error.
//! - `--codex-app-server <state> [args…]`: `codex app-server --listen stdio://` for the account read: records its argv in
//!   `<state>/codex-argv.json` and each line it reads in `<state>/codex-stdin.jsonl`, answers `initialize`, `account/read` and
//!   `account/rateLimits/read`, and ends once its input closes.
//! - Copied as `claude`, `codex` or `agy` (its own file name says which) with `MIGHTY_ACCOUNTS_FAKE_STATE` set: that CLI's status
//!   and logout commands. Each call is a line of `<state>/calls.jsonl`; `claude auth logout` leaves `<state>/claude-signed-out`, which
//!   `claude auth status --json` then reports.

use std::io::{BufRead, Write};
use std::path::{Path, PathBuf};

use serde_json::{Value, json};

/// agy 1.5.5's `/usage` reply, as the Mac's checks quote it.
const USAGE: &str = r#"{"conversation_id":"","status":"SUCCESS","response":"group\tbucket","usage":{"input_tokens":0},"command":{"name":"usage","data":{"description":"Within each group, models share a weekly limit and a 5-hour limit.","groups":[{"name":"Gemini Models","description":"Models within this group: Gemini Flash, Gemini Pro","buckets":[{"id":"gemini-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":0.983944296836853,"reset_time":"2026-11-14T03:59:25Z"},{"id":"gemini-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":0.9997177720069885,"reset_time":"2026-11-10T17:58:15Z"}]},{"name":"Claude and GPT models","description":"Models within this group: Claude Opus, Claude Sonnet, GPT-OSS","buckets":[{"id":"3p-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":1,"reset_time":"2026-11-17T13:01:31Z"},{"id":"3p-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":1,"reset_time":"2026-11-10T18:01:31Z"}]}]}}}"#;

fn write(path: &Path, text: &str) {
    let _ = std::fs::write(path, text);
}

fn variable(name: &str) -> Value {
    std::env::var(name).map(Value::String).unwrap_or(Value::Null)
}

fn agy_usage(state: &Path, args: &[String]) -> i32 {
    write(&state.join("agy-argv.json"), &json!(args).to_string());
    let folder = state.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let marker = format!("mighty-agy-usage-{folder}.marker");
    let here = std::env::current_dir().map(|d| d.join(&marker).is_file()).unwrap_or(false);
    let run = json!({"NO_COLOR": variable("NO_COLOR"), "CI": variable("CI"), "inTemporaryFolder": if here { "yes" } else { "no" }});
    write(&state.join("agy-run.json"), &run.to_string());
    if args != ["-p", "/usage", "--output-format", "json"] {
        eprintln!("Error: unexpected arguments");
        return 2;
    }
    if state.join("agy-signed-out").exists() {
        eprintln!("Error: Please sign in to view your usage. Launch the CLI without arguments to sign in.");
        return 1;
    }
    println!("{USAGE}");
    0
}

fn codex_app_server(state: &Path, args: &[String]) -> i32 {
    write(&state.join("codex-argv.json"), &json!(args).to_string());
    let mut seen = std::fs::OpenOptions::new().create(true).append(true).open(state.join("codex-stdin.jsonl")).ok();
    let stdin = std::io::stdin();
    let mut out = std::io::stdout().lock();
    for line in stdin.lock().lines() {
        let Ok(line) = line else { break };
        if let Some(file) = seen.as_mut() {
            let _ = writeln!(file, "{line}");
        }
        let Ok(message) = serde_json::from_str::<Value>(&line) else { continue };
        let answer = match message.get("id").and_then(Value::as_i64) {
            Some(1) => json!({"id": 1, "result": {"userAgent": "fixture"}}),
            Some(2) => json!({"id": 2, "result": {"account": {"type": "chatgpt", "email": "fixture@example.test", "planType": "plus"}}}),
            Some(3) => {
                json!({"id": 3, "result": {"rateLimitsByLimitId": {"codex": {"planType": "pro", "primary": {"usedPercent": 8, "windowDurationMins": 300, "resetsAt": 1_790_000_000}, "secondary": {"usedPercent": 61, "windowDurationMins": 10080}}}}})
            }
            _ => continue,
        };
        let _ = writeln!(out, "{answer}");
        let _ = out.flush();
    }
    0
}

fn account_cli(name: &str, state: &Path, args: &[String]) -> i32 {
    let call = json!({
        "name": name,
        "args": args,
        "NO_COLOR": variable("NO_COLOR"),
        "CI": variable("CI"),
        "cwd": std::env::current_dir().map(|d| d.to_string_lossy().into_owned()).unwrap_or_default(),
    });
    if let Ok(mut file) = std::fs::OpenOptions::new().create(true).append(true).open(state.join("calls.jsonl")) {
        let _ = writeln!(file, "{call}");
    }
    let words: Vec<&str> = args.iter().map(String::as_str).collect();
    match (name, words.as_slice()) {
        ("claude", ["auth", "status", "--json"]) => {
            if state.join("claude-signed-out").exists() {
                println!(r#"{{"loggedIn":false}}"#);
            } else {
                println!(r#"{{"loggedIn":true,"authMethod":"claude.ai","email":"me@example.com","subscriptionType":"max"}}"#);
            }
            0
        }
        ("claude", ["auth", "logout"]) => {
            write(&state.join("claude-signed-out"), "");
            0
        }
        ("codex", ["login", "status"]) => {
            eprintln!("Logged in using ChatGPT");
            0
        }
        ("agy", ["models"]) => {
            eprintln!("Error: authentication required. Run 'agy' to log in, then retry.");
            1
        }
        _ => {
            eprintln!("unexpected: {name} {}", words.join(" "));
            2
        }
    }
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let code = match args.first().map(String::as_str) {
        Some("--agy-usage-cli") if args.len() >= 2 => agy_usage(&PathBuf::from(&args[1]), &args[2..]),
        Some("--codex-app-server") if args.len() >= 2 => codex_app_server(&PathBuf::from(&args[1]), &args[2..]),
        _ => {
            let name = std::env::current_exe()
                .ok()
                .and_then(|path| path.file_stem().map(|stem| stem.to_string_lossy().to_lowercase()))
                .unwrap_or_default();
            match std::env::var("MIGHTY_ACCOUNTS_FAKE_STATE") {
                Ok(state) if matches!(name.as_str(), "claude" | "codex" | "agy") => account_cli(&name, Path::new(&state), &args),
                _ => 0,
            }
        }
    };
    std::process::exit(code);
}
